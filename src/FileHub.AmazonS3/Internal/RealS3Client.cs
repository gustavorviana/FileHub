using Amazon.S3;
using Amazon.S3.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.AmazonS3.Internal
{
    /// <summary>
    /// Concrete <see cref="IS3Client"/> backed by the AWSSDK
    /// <see cref="IAmazonS3"/>. Translates SDK exceptions into
    /// framework-friendly ones (<see cref="FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/>) at the boundary so no
    /// SDK types leak past this class.
    /// </summary>
    internal sealed class RealS3Client : IS3Client
    {
        private readonly IAmazonS3 _client;
        private readonly bool _ownsClient;
        private readonly object _credentialScope;
        private bool _disposed;

        public string Bucket { get; }
        public string Region { get; }
        public object CredentialScope => _credentialScope;

        public RealS3Client(IAmazonS3 client, string bucket, string region, bool ownsClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            Bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
            Region = region ?? throw new ArgumentNullException(nameof(region));
            _ownsClient = ownsClient;
            // When the caller owns the client, reuse its reference as the scope
            // so two hubs built around the same external IAmazonS3 can exchange
            // server-side CopyObject calls. When this class owns the client,
            // create a dedicated scope object so two hubs built by distinct
            // factory calls do not accidentally share it.
            _credentialScope = ownsClient ? new object() : client;
        }

        public async Task<S3HeadResult> HeadObjectAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var resp = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = Bucket,
                    Key = key
                }, cancellationToken).ConfigureAwait(false);

                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in resp.Metadata.Keys)
                    meta[k] = resp.Metadata[k];

                return new S3HeadResult
                {
                    ContentLength = resp.ContentLength,
                    LastModified = resp.LastModified,
                    ContentType = resp.Headers.ContentType,
                    CacheControl = resp.Headers.CacheControl,
                    UserMetadata = meta,
                    StorageClass = resp.StorageClass?.Value,
                    ServerSideEncryption = resp.ServerSideEncryptionMethod?.Value
                };
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public async Task<S3GetResult> GetObjectAsync(string key, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var req = new GetObjectRequest { BucketName = Bucket, Key = key };
                if (rangeStart.HasValue && rangeEndInclusive.HasValue)
                    req.ByteRange = new ByteRange(rangeStart.Value, rangeEndInclusive.Value);

                var resp = await _client.GetObjectAsync(req, cancellationToken).ConfigureAwait(false);
                return new S3GetResult { InputStream = resp.ResponseStream };
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public async Task PutObjectAsync(
            string key, Stream body, long contentLength,
            S3WriteOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var req = new PutObjectRequest
                {
                    BucketName = Bucket,
                    Key = key,
                    InputStream = body,
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false
                };
                if (contentLength >= 0) req.Headers.ContentLength = contentLength;
                ApplyOptions(req, options);
                await _client.PutObjectAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        private static void ApplyOptions(PutObjectRequest req, S3WriteOptions options)
        {
            if (options == null) return;
            if (!string.IsNullOrEmpty(options.ContentType)) req.ContentType = options.ContentType;
            if (!string.IsNullOrEmpty(options.CacheControl)) req.Headers.CacheControl = options.CacheControl;
            if (options.Metadata != null)
            {
                foreach (var kv in options.Metadata)
                    req.Metadata.Add(kv.Key, kv.Value ?? string.Empty);
            }
            if (!string.IsNullOrEmpty(options.StorageClass))
                req.StorageClass = S3StorageClass.FindValue(options.StorageClass);
            if (!string.IsNullOrEmpty(options.ServerSideEncryption))
                req.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(options.ServerSideEncryption);
        }

        public async Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key }, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public async Task<IReadOnlyList<S3DeleteError>> DeleteObjectsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (keys.Count == 0) return System.Array.Empty<S3DeleteError>();
            if (keys.Count > 1000)
                throw new ArgumentException("S3 DeleteObjects accepts at most 1000 keys per call.", nameof(keys));

            try
            {
                var req = new DeleteObjectsRequest { BucketName = Bucket };
                foreach (var k in keys)
                    req.Objects.Add(new KeyVersion { Key = k });
                // Quiet mode: response only contains errors, not successful keys —
                // smaller payload, cheaper to parse for our use case.
                req.Quiet = true;

                var resp = await _client.DeleteObjectsAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.DeleteErrors == null || resp.DeleteErrors.Count == 0)
                    return System.Array.Empty<S3DeleteError>();

                var errors = new List<S3DeleteError>(resp.DeleteErrors.Count);
                foreach (var e in resp.DeleteErrors)
                    errors.Add(new S3DeleteError { Key = e.Key, Code = e.Code, Message = e.Message });
                return errors;
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, keys.Count > 0 ? keys[0] : string.Empty);
            }
        }

        public async Task CopyFromBucketAsync(
            string sourceBucket, string sourceKey, string destinationKey,
            bool metadataReplace,
            S3WriteOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                // Request is issued to THIS client's endpoint, which is the
                // destination region — correct for cross-region CopyObject.
                var req = new CopyObjectRequest
                {
                    SourceBucket = sourceBucket,
                    SourceKey = sourceKey,
                    DestinationBucket = Bucket,
                    DestinationKey = destinationKey
                };
                if (metadataReplace && options != null)
                {
                    req.MetadataDirective = S3MetadataDirective.REPLACE;
                    if (!string.IsNullOrEmpty(options.ContentType)) req.ContentType = options.ContentType;
                    if (options.Metadata != null)
                    {
                        foreach (var kv in options.Metadata)
                            req.Metadata.Add(kv.Key, kv.Value ?? string.Empty);
                    }
                }
                else if (metadataReplace)
                {
                    req.MetadataDirective = S3MetadataDirective.REPLACE;
                }
                // Storage class + SSE are independent of MetadataDirective in S3;
                // applied on the destination regardless of REPLACE vs COPY.
                if (options != null)
                {
                    if (!string.IsNullOrEmpty(options.StorageClass))
                        req.StorageClass = S3StorageClass.FindValue(options.StorageClass);
                    if (!string.IsNullOrEmpty(options.ServerSideEncryption))
                        req.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(options.ServerSideEncryption);
                }

                await _client.CopyObjectAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, sourceKey);
            }
        }

        public async Task<S3ListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string continuationToken, string startAfter, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var req = new ListObjectsV2Request
                {
                    BucketName = Bucket,
                    Prefix = prefix,
                    Delimiter = delimiter,
                    ContinuationToken = continuationToken
                };
                // S3 honors StartAfter only on the first page of a listing —
                // once a ContinuationToken is in play, the cursor travels in
                // the token and StartAfter is ignored.
                if (string.IsNullOrEmpty(continuationToken) && !string.IsNullOrEmpty(startAfter))
                    req.StartAfter = startAfter;
                if (limit.HasValue) req.MaxKeys = limit.Value;

                var resp = await _client.ListObjectsV2Async(req, cancellationToken).ConfigureAwait(false);

                var page = new S3ListPage
                {
                    NextContinuationToken = resp.IsTruncated ? resp.NextContinuationToken ?? continuationToken : null
                };
                if (resp.S3Objects != null)
                {
                    foreach (var obj in resp.S3Objects)
                        page.Objects.Add(new S3ListObject { Key = obj.Key, Size = obj.Size, LastModified = obj.LastModified });
                }
                if (resp.CommonPrefixes != null)
                {
                    foreach (var cp in resp.CommonPrefixes)
                        page.Prefixes.Add(cp);
                }
                return page;
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, prefix ?? "<root>");
            }
        }

        public async Task<S3BucketInfo> GetBucketAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var resp = await _client.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest
                {
                    BucketName = Bucket
                }, cancellationToken).ConfigureAwait(false);
                return new S3BucketInfo { IsPublic = resp.PolicyStatus?.IsPublic ?? false };
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucketPolicy")
            {
                return new S3BucketInfo { IsPublic = false };
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, Bucket);
            }
        }

        public Task<string> GetPreSignedUrlAsync(string key, DateTime timeExpiresUtc, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = key,
                Expires = timeExpiresUtc,
                Verb = HttpVerb.GET
            });
            return Task.FromResult(url);
        }

        public Task<string> GetPreSignedUploadUrlAsync(
            string key,
            DateTime timeExpiresUtc,
            S3WriteOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var req = new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = key,
                Expires = timeExpiresUtc,
                Verb = HttpVerb.PUT,
            };
            // Each populated option is folded into the signature; the client
            // must send the matching header on PUT or S3 returns
            // SignatureDoesNotMatch.
            if (options != null)
            {
                if (!string.IsNullOrEmpty(options.ContentType)) req.ContentType = options.ContentType;
                if (!string.IsNullOrEmpty(options.CacheControl)) req.Headers.CacheControl = options.CacheControl;
                if (options.Metadata != null)
                {
                    foreach (var kv in options.Metadata)
                        req.Metadata.Add(kv.Key, kv.Value ?? string.Empty);
                }
                if (!string.IsNullOrEmpty(options.StorageClass)) req.Headers["x-amz-storage-class"] = options.StorageClass;
                if (!string.IsNullOrEmpty(options.ServerSideEncryption))
                    req.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(options.ServerSideEncryption);
            }

            var url = _client.GetPreSignedURL(req);
            return Task.FromResult(url);
        }

        public async Task<string> BeginMultipartUploadAsync(
            string key,
            S3WriteOptions options,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var req = new InitiateMultipartUploadRequest { BucketName = Bucket, Key = key };
                if (options != null)
                {
                    if (!string.IsNullOrEmpty(options.ContentType)) req.ContentType = options.ContentType;
                    if (!string.IsNullOrEmpty(options.CacheControl)) req.Headers.CacheControl = options.CacheControl;
                    if (options.Metadata != null)
                    {
                        foreach (var kv in options.Metadata)
                            req.Metadata.Add(kv.Key, kv.Value ?? string.Empty);
                    }
                    if (!string.IsNullOrEmpty(options.StorageClass))
                        req.StorageClass = S3StorageClass.FindValue(options.StorageClass);
                    if (!string.IsNullOrEmpty(options.ServerSideEncryption))
                        req.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(options.ServerSideEncryption);
                }
                var resp = await _client.InitiateMultipartUploadAsync(req, cancellationToken).ConfigureAwait(false);
                return resp.UploadId;
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public async Task<string> UploadPartAsync(string key, string uploadId, int partNumber, Stream body, long contentLength, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var resp = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    InputStream = body,
                    PartSize = contentLength
                }, cancellationToken).ConfigureAwait(false);
                return resp.ETag;
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public async Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<S3CompletedPart> parts, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                var req = new CompleteMultipartUploadRequest
                {
                    BucketName = Bucket,
                    Key = key,
                    UploadId = uploadId
                };
                foreach (var p in parts)
                    req.AddPartETags(new PartETag(p.PartNumber, p.ETag));

                await _client.CompleteMultipartUploadAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public async Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = Bucket,
                    Key = key,
                    UploadId = uploadId
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                throw TranslateException(ex, key);
            }
        }

        public Task<Uri> GetPreSignedUploadPartUrlAsync(string key, string uploadId, int partNumber, DateTime expiresUtc, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = key,
                Expires = expiresUtc,
                Verb = HttpVerb.PUT,
                UploadId = uploadId,
                PartNumber = partNumber
            });
            return Task.FromResult(new Uri(url));
        }

        private static Exception TranslateException(AmazonS3Exception ex, string context)
        {
            var code = ex.ErrorCode ?? string.Empty;

            if (ex.StatusCode == HttpStatusCode.NotFound
                || string.Equals(code, "NoSuchKey", StringComparison.Ordinal)
                || string.Equals(code, "NoSuchBucket", StringComparison.Ordinal)
                || string.Equals(code, "NoSuchUpload", StringComparison.Ordinal))
                return new FileNotFoundException($"Object \"{context}\" not found.", ex);

            if (ex.StatusCode == HttpStatusCode.Forbidden
                || ex.StatusCode == HttpStatusCode.Unauthorized
                || string.Equals(code, "AccessDenied", StringComparison.Ordinal)
                || string.Equals(code, "SignatureDoesNotMatch", StringComparison.Ordinal)
                || string.Equals(code, "InvalidAccessKeyId", StringComparison.Ordinal)
                || string.Equals(code, "ExpiredToken", StringComparison.Ordinal)
                || string.Equals(code, "TokenRefreshRequired", StringComparison.Ordinal))
                return new UnauthorizedAccessException($"Access denied for \"{context}\".", ex);

            var requestId = string.IsNullOrEmpty(ex.RequestId) ? "" : $" (request-id={ex.RequestId})";
            var status = ex.StatusCode == 0 ? (HttpStatusCode?)null : ex.StatusCode;
            return new AmazonS3DriverException(
                $"S3 request failed for \"{context}\": [{(string.IsNullOrEmpty(code) ? "unknown" : code)}] {ex.Message}{requestId}",
                status,
                string.IsNullOrEmpty(code) ? null : code,
                ex.RequestId,
                ex);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsClient)
                _client.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealS3Client));
        }
    }
}
