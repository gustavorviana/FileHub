using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Oci.Common.Model;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Requests;
using static Oci.ObjectstorageService.Models.Bucket;
using static Oci.ObjectstorageService.Models.CreatePreauthenticatedRequestDetails;

namespace FileHub.OracleObjectStorage.Internal
{
    /// <summary>
    /// <see cref="IOciClient"/> implementation backed by the real
    /// <see cref="ObjectStorageClient"/> from the OCI .NET SDK. All SDK-specific
    /// exceptions are translated into BCL / FileHub exceptions inside this class
    /// so consumers only see <see cref="FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="FileHubException"/>.
    /// </summary>
    internal sealed class RealOciClient : IOciClient
    {
        private readonly ObjectStorageClient _client;
        private readonly bool _ownsClient;
        private volatile bool _disposed;

        public string Namespace { get; }
        public string Bucket { get; }
        public string Region { get; }

        public object CredentialScope => _client;

        public RealOciClient(ObjectStorageClient client, string @namespace, string bucket, string region, bool ownsClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
            Bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
            Region = region ?? throw new ArgumentNullException(nameof(region));
            _ownsClient = ownsClient;
        }

        public async Task<OciHeadResult> HeadObjectAsync(string objectName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return await TranslateAsync(objectName, async ct =>
            {
                var resp = await _client.HeadObject(new HeadObjectRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    ObjectName = objectName
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);

                return new OciHeadResult
                {
                    ContentLength = resp.ContentLength,
                    LastModified = resp.LastModified,
                    ContentType = resp.ContentType,
                    OpcMeta = resp.OpcMeta != null ? new Dictionary<string, string>(resp.OpcMeta) : null
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<OciGetResult> GetObjectAsync(string objectName, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return await TranslateAsync(objectName, async ct =>
            {
                Oci.Common.Model.Range range = null;
                if (rangeStart.HasValue && rangeEndInclusive.HasValue)
                    range = new Oci.Common.Model.Range { StartByte = rangeStart.Value, EndByte = rangeEndInclusive.Value };

                var resp = await _client.GetObject(new GetObjectRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    ObjectName = objectName,
                    Range = range
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);

                return new OciGetResult { InputStream = resp.InputStream };
            }, cancellationToken).ConfigureAwait(false);
        }

        public Task PutObjectAsync(
            string objectName,
            Stream body,
            long contentLength,
            string contentType,
            IReadOnlyDictionary<string, string> opcMeta,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(objectName, async ct =>
            {
                var request = new PutObjectRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    ObjectName = objectName,
                    PutObjectBody = body,
                    ContentLength = contentLength,
                    ContentType = contentType,
                    OpcMeta = CopyMeta(opcMeta)
                };
                await _client.PutObject(request, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task DeleteObjectAsync(string objectName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(objectName, async ct =>
            {
                await _client.DeleteObject(new DeleteObjectRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    ObjectName = objectName
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task RenameObjectAsync(string sourceName, string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(sourceName, async ct =>
            {
                await _client.RenameObject(new RenameObjectRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    RenameObjectDetails = new Oci.ObjectstorageService.Models.RenameObjectDetails
                    {
                        SourceName = sourceName,
                        NewName = newName
                    }
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task CopyObjectAsync(
            string sourceObjectName,
            string destinationNamespace,
            string destinationBucket,
            string destinationRegion,
            string destinationObjectName,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(sourceObjectName, async ct =>
            {
                await _client.CopyObject(new CopyObjectRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    CopyObjectDetails = new Oci.ObjectstorageService.Models.CopyObjectDetails
                    {
                        SourceObjectName = sourceObjectName,
                        DestinationRegion = destinationRegion,
                        DestinationNamespace = destinationNamespace,
                        DestinationBucket = destinationBucket,
                        DestinationObjectName = destinationObjectName
                    }
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public async Task<OciListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string start, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return await TranslateAsync(prefix ?? "<root>", async ct =>
            {
                var resp = await _client.ListObjects(new ListObjectsRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    Prefix = prefix,
                    Delimiter = delimiter,
                    Limit = limit,
                    Start = start,
                    Fields = "name,size,timeCreated"
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);

                var page = new OciListPage
                {
                    NextStartWith = resp.ListObjects.NextStartWith
                };
                if (resp.ListObjects.Objects != null)
                {
                    foreach (var o in resp.ListObjects.Objects)
                    {
                        page.Objects.Add(new OciListObject
                        {
                            Name = o.Name,
                            Size = o.Size,
                            TimeCreated = o.TimeCreated
                        });
                    }
                }
                if (resp.ListObjects.Prefixes != null)
                    page.Prefixes.AddRange(resp.ListObjects.Prefixes);
                return page;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<OciBucketInfo> GetBucketAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return await TranslateAsync("<bucket>", async ct =>
            {
                var resp = await _client.GetBucket(new GetBucketRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);

                var accessType = resp.Bucket.PublicAccessType switch
                {
                    PublicAccessTypeEnum.ObjectRead => OciBucketAccessType.ObjectRead,
                    PublicAccessTypeEnum.ObjectReadWithoutList => OciBucketAccessType.ObjectReadWithoutList,
                    _ => OciBucketAccessType.NoPublicAccess
                };
                return new OciBucketInfo { PublicAccessType = accessType };
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> CreatePreauthenticatedReadRequestAsync(string objectName, string parName, DateTime timeExpiresUtc, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return await TranslateAsync(objectName, async ct =>
            {
                var resp = await _client.CreatePreauthenticatedRequest(new CreatePreauthenticatedRequestRequest
                {
                    NamespaceName = Namespace,
                    BucketName = Bucket,
                    CreatePreauthenticatedRequestDetails = new Oci.ObjectstorageService.Models.CreatePreauthenticatedRequestDetails
                    {
                        Name = parName,
                        ObjectName = objectName,
                        AccessType = AccessTypeEnum.ObjectRead,
                        TimeExpires = timeExpiresUtc
                    }
                }, retryConfiguration: null, cancellationToken: ct).ConfigureAwait(false);
                return resp.PreauthenticatedRequest.AccessUri;
            }, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsClient) _client.Dispose();
        }

        // --- Error translation ---

        private async Task<T> TranslateAsync<T>(string contextObject, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldTranslate(ex))
            {
                throw Translate(ex, contextObject);
            }
        }

        private async Task TranslateAsync(string contextObject, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldTranslate(ex))
            {
                throw Translate(ex, contextObject);
            }
        }

        private static bool ShouldTranslate(Exception ex)
        {
            // Let cancellation and programmer errors bubble through untouched;
            // translate everything else at the SDK boundary so no OCI type
            // leaks out of this class.
            return !(ex is OperationCanceledException)
                && !(ex is ArgumentException)
                && !(ex is ObjectDisposedException);
        }

        private Exception Translate(Exception raw, string contextObject)
        {
            var oe = raw as OciException;
            var status = oe?.StatusCode;
            var serviceCode = oe?.ServiceCode;
            var opcRequestId = oe?.OpcRequestId;

            if (status == HttpStatusCode.NotFound
                || string.Equals(serviceCode, "BucketNotFound", StringComparison.Ordinal)
                || string.Equals(serviceCode, "NamespaceNotFound", StringComparison.Ordinal)
                || string.Equals(serviceCode, "ObjectNotFound", StringComparison.Ordinal)
                || MessageIndicatesNotFound(raw.Message))
                return new FileNotFoundException(
                    $"Object \"{contextObject}\" not found in bucket \"{Bucket}\" (namespace \"{Namespace}\").",
                    raw);

            if (status == HttpStatusCode.Unauthorized
                || status == HttpStatusCode.Forbidden
                || string.Equals(serviceCode, "NotAuthenticated", StringComparison.Ordinal)
                || string.Equals(serviceCode, "NotAuthorized", StringComparison.Ordinal)
                || string.Equals(serviceCode, "NotAuthorizedOrNotFound", StringComparison.Ordinal)
                || string.Equals(serviceCode, "SignatureDoesNotMatch", StringComparison.Ordinal))
                return new UnauthorizedAccessException(
                    $"Access denied for \"{contextObject}\" in bucket \"{Bucket}\": {raw.Message}",
                    raw);

            var requestIdSuffix = string.IsNullOrEmpty(opcRequestId) ? "" : $" (opc-request-id={opcRequestId})";
            var codeSuffix = string.IsNullOrEmpty(serviceCode) ? "" : $"[{serviceCode}] ";
            return new OciDriverException(
                $"OCI request failed for \"{contextObject}\" in bucket \"{Bucket}\": {codeSuffix}{raw.Message}{requestIdSuffix}",
                status,
                serviceCode,
                opcRequestId,
                raw);
        }

        private static Dictionary<string, string> CopyMeta(IReadOnlyDictionary<string, string> source)
        {
            if (source == null) return null;
            var copy = new Dictionary<string, string>(source.Count);
            foreach (var kvp in source) copy[kvp.Key] = kvp.Value;
            return copy;
        }

        private static bool MessageIndicatesNotFound(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("was not found", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("does not exist in the namespace", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("Not Found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealOciClient));
        }
    }
}
