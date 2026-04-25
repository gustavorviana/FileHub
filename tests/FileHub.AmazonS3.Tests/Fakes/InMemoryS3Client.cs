using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3.Tests.Fakes;

/// <summary>
/// Deterministic in-memory <see cref="IS3Client"/> implementation for
/// unit tests. Mirrors S3 semantics: flat key/value store, common-prefix
/// enumeration with delimiter, opaque continuation tokens, and full
/// multipart upload lifecycle.
/// </summary>
/// <remarks>
/// Two construction modes:
/// <list type="bullet">
///   <item>Stand-alone: <c>new InMemoryS3Client(...)</c> — each instance
///   owns its bucket store and has a distinct <c>CredentialScope</c>.</item>
///   <item>World-backed: <see cref="InWorld"/> — the client borrows a
///   bucket store from a shared <see cref="InMemoryS3World"/>, and all
///   clients in the same world share their <c>CredentialScope</c>.</item>
/// </list>
/// </remarks>
internal sealed class InMemoryS3Client : IS3Client
{
    private readonly InMemoryS3World? _world;
    private readonly object _ownScope;
    private readonly InMemoryS3BucketStore _store;
    private bool _disposed;
    private int _copyInvocationCount;
    private int _headInvocationCount;
    private int _putInvocationCount;
    private long _uploadCounter;

    public string Bucket { get; }
    public string Region { get; }

    public object CredentialScope => (object?)_world ?? _ownScope;

    public int CopyInvocationCount => _copyInvocationCount;
    public int HeadInvocationCount => _headInvocationCount;
    public int PutInvocationCount => _putInvocationCount;

    public Func<string, Exception?>? DeleteFailureInjector { get; set; }

    public InMemoryS3Client(string bucket = "test-bucket", string region = "us-test-1")
    {
        _world = null;
        _ownScope = new object();
        _store = new InMemoryS3BucketStore();
        Bucket = bucket;
        Region = region;
    }

    private InMemoryS3Client(InMemoryS3World world, string bucket, string region)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _ownScope = null!;
        _store = world.GetOrCreateStore(bucket);
        Bucket = bucket;
        Region = region;
    }

    internal static InMemoryS3Client InWorld(InMemoryS3World world, string bucket, string region)
        => new(world, bucket, region);

    public void SetIsPublic(bool value) => _store.IsPublic = value;

    public int ObjectCount => _store.Objects.Count;
    public IReadOnlyCollection<string> Keys => _store.Objects.Keys.ToArray();

    public bool TryGetBody(string key, out byte[] body)
    {
        if (_store.Objects.TryGetValue(key, out var obj))
        {
            body = obj.Body;
            return true;
        }
        body = null!;
        return false;
    }

    public Task<S3HeadResult> HeadObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _headInvocationCount);
        if (!_store.Objects.TryGetValue(key, out var obj))
            throw new FileNotFoundException($"Object \"{key}\" not found.");

        return Task.FromResult(new S3HeadResult
        {
            ContentLength = obj.Body.LongLength,
            LastModified = obj.LastModified,
            ContentType = obj.ContentType,
            UserMetadata = obj.UserMetadata is null
                ? null
                : new Dictionary<string, string>(obj.UserMetadata, StringComparer.OrdinalIgnoreCase),
            StorageClass = obj.StorageClass,
            ServerSideEncryption = obj.ServerSideEncryption
        });
    }

    public Task<S3GetResult> GetObjectAsync(string key, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.Objects.TryGetValue(key, out var obj))
            throw new FileNotFoundException($"Object \"{key}\" not found.");

        byte[] slice;
        if (rangeStart.HasValue && rangeEndInclusive.HasValue)
        {
            long start = rangeStart.Value;
            long endInclusive = rangeEndInclusive.Value;
            if (start < 0 || endInclusive < start || endInclusive >= obj.Body.LongLength)
                throw new IOException($"Invalid range {start}-{endInclusive} against length {obj.Body.LongLength}.");

            int len = checked((int)(endInclusive - start + 1));
            slice = new byte[len];
            Array.Copy(obj.Body, start, slice, 0, len);
        }
        else
        {
            slice = (byte[])obj.Body.Clone();
        }

        return Task.FromResult(new S3GetResult { InputStream = new MemoryStream(slice, writable: false) });
    }

    public async Task PutObjectAsync(
        string key,
        Stream body,
        long contentLength,
        string contentType,
        IReadOnlyDictionary<string, string> userMetadata,
        string storageClass,
        string serverSideEncryption,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _putInvocationCount);

        byte[] bytes;
        if (body is null)
        {
            bytes = Array.Empty<byte>();
        }
        else
        {
            using var ms = new MemoryStream();
            await body.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        if (contentLength >= 0 && contentLength != bytes.LongLength)
            bytes = bytes.Take(checked((int)contentLength)).ToArray();

        _store.Objects[key] = new InMemoryS3StoredObject
        {
            Body = bytes,
            ContentType = contentType,
            UserMetadata = userMetadata is null
                ? null
                : new Dictionary<string, string>(userMetadata, StringComparer.OrdinalIgnoreCase),
            LastModified = DateTime.UtcNow,
            StorageClass = storageClass,
            ServerSideEncryption = serverSideEncryption
        };
    }

    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var injected = DeleteFailureInjector?.Invoke(key);
        if (injected is not null)
            throw injected;
        if (!_store.Objects.TryRemove(key, out _))
            throw new FileNotFoundException($"Object \"{key}\" not found.");
        return Task.CompletedTask;
    }

    public Task CopyFromBucketAsync(
        string sourceBucket,
        string sourceKey,
        string destinationKey,
        bool metadataReplace,
        string contentType,
        IReadOnlyDictionary<string, string> userMetadata,
        string storageClass,
        string serverSideEncryption,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _copyInvocationCount);

        // THIS client is the destination. Fetch source from the matching
        // store — our own if same bucket, the world's if different.
        InMemoryS3BucketStore sourceStore;
        if (string.Equals(sourceBucket, Bucket, StringComparison.Ordinal))
        {
            sourceStore = _store;
        }
        else if (_world is not null)
        {
            sourceStore = _world.GetOrCreateStore(sourceBucket);
        }
        else
        {
            throw new InvalidOperationException(
                "Cross-bucket CopyFromBucket on a scope-less InMemoryS3Client — the driver should have fallen back to stream copy.");
        }

        if (!sourceStore.Objects.TryGetValue(sourceKey, out var obj))
            throw new FileNotFoundException($"Object \"{sourceKey}\" not found in bucket \"{sourceBucket}\".");

        // MetadataDirective = REPLACE → use supplied fields; otherwise inherit from source.
        var destContentType = metadataReplace ? contentType : obj.ContentType;
        Dictionary<string, string> destMeta;
        if (metadataReplace)
        {
            destMeta = userMetadata is null
                ? null
                : new Dictionary<string, string>(userMetadata, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            destMeta = obj.UserMetadata is null
                ? null
                : new Dictionary<string, string>(obj.UserMetadata, StringComparer.OrdinalIgnoreCase);
        }
        // StorageClass / SSE are independent of MetadataDirective in real S3.
        // If caller specified values, use them; otherwise inherit from source.
        var destStorage = string.IsNullOrEmpty(storageClass) ? obj.StorageClass : storageClass;
        var destSse = string.IsNullOrEmpty(serverSideEncryption) ? obj.ServerSideEncryption : serverSideEncryption;

        _store.Objects[destinationKey] = new InMemoryS3StoredObject
        {
            Body = (byte[])obj.Body.Clone(),
            ContentType = destContentType,
            UserMetadata = destMeta,
            LastModified = DateTime.UtcNow,
            StorageClass = destStorage,
            ServerSideEncryption = destSse
        };
        return Task.CompletedTask;
    }

    public Task<S3ListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string continuationToken, string startAfter, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        prefix ??= string.Empty;
        // Mirrors S3: StartAfter is exclusive and only honored on page 1.
        var effectiveStartAfter = string.IsNullOrEmpty(continuationToken) ? startAfter : null;
        var orderedKeys = _store.Objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Where(k => string.IsNullOrEmpty(continuationToken) || string.CompareOrdinal(k, continuationToken) >= 0)
            .Where(k => string.IsNullOrEmpty(effectiveStartAfter) || string.CompareOrdinal(k, effectiveStartAfter) > 0)
            .OrderBy(k => k, StringComparer.Ordinal);

        var page = new S3ListPage();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;

        foreach (var key in orderedKeys)
        {
            if (limit.HasValue && count >= limit.Value)
            {
                page.NextContinuationToken = key;
                break;
            }

            if (!string.IsNullOrEmpty(delimiter))
            {
                var rest = key.Substring(prefix.Length);
                int delimIdx = rest.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimIdx >= 0)
                {
                    var commonPrefix = prefix + rest.Substring(0, delimIdx + delimiter.Length);
                    if (seenPrefixes.Add(commonPrefix))
                    {
                        page.Prefixes.Add(commonPrefix);
                        count++;
                    }
                    continue;
                }
            }

            _store.Objects.TryGetValue(key, out var obj);
            page.Objects.Add(new S3ListObject
            {
                Key = key,
                Size = obj?.Body.LongLength ?? 0,
                LastModified = obj?.LastModified
            });
            count++;
        }

        return Task.FromResult(page);
    }

    public Task<S3BucketInfo> GetBucketAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new S3BucketInfo { IsPublic = _store.IsPublic });
    }

    public Task<string> GetPreSignedUrlAsync(string key, DateTime timeExpiresUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Uri.EscapeDataString(key).Replace("%2F", "/");
        var expires = ((DateTimeOffset)timeExpiresUtc).ToUnixTimeSeconds();
        return Task.FromResult($"https://{Bucket}.s3.{Region}.amazonaws.com/{encoded}?X-Amz-Expires={expires}&X-Amz-Signature=test");
    }

    public Task<string> BeginMultipartUploadAsync(
        string key,
        string contentType,
        IReadOnlyDictionary<string, string> userMetadata,
        string storageClass,
        string serverSideEncryption,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var uploadId = $"upload-{Interlocked.Increment(ref _uploadCounter)}";
        _store.Uploads[uploadId] = new InMemoryS3MultipartUpload
        {
            UploadId = uploadId,
            Key = key,
            ContentType = contentType,
            UserMetadata = userMetadata is null
                ? null
                : new Dictionary<string, string>(userMetadata, StringComparer.OrdinalIgnoreCase),
            StorageClass = storageClass,
            ServerSideEncryption = serverSideEncryption
        };
        return Task.FromResult(uploadId);
    }

    public async Task<string> UploadPartAsync(string key, string uploadId, int partNumber, Stream body, long contentLength, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.Uploads.TryGetValue(uploadId, out var upload))
            throw new InvalidOperationException($"No multipart upload with id \"{uploadId}\".");
        if (!string.Equals(upload.Key, key, StringComparison.Ordinal))
            throw new InvalidOperationException($"Upload \"{uploadId}\" is for key \"{upload.Key}\", not \"{key}\".");

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await body.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }
        if (contentLength >= 0 && bytes.LongLength > contentLength)
            bytes = bytes.Take(checked((int)contentLength)).ToArray();

        var etag = $"\"etag-{uploadId}-{partNumber}\"";
        upload.Parts[partNumber] = new InMemoryS3UploadedPart { Body = bytes, ETag = etag };
        return etag;
    }

    public Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<S3CompletedPart> parts, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.Uploads.TryRemove(uploadId, out var upload))
            throw new InvalidOperationException($"No multipart upload with id \"{uploadId}\".");
        if (!string.Equals(upload.Key, key, StringComparison.Ordinal))
            throw new InvalidOperationException($"Upload \"{uploadId}\" is for key \"{upload.Key}\", not \"{key}\".");

        using var ms = new MemoryStream();
        foreach (var p in parts.OrderBy(p => p.PartNumber))
        {
            if (!upload.Parts.TryGetValue(p.PartNumber, out var storedPart))
                throw new InvalidOperationException($"Part {p.PartNumber} was never uploaded for \"{uploadId}\".");
            if (!string.Equals(storedPart.ETag, p.ETag, StringComparison.Ordinal))
                throw new InvalidOperationException($"ETag mismatch for part {p.PartNumber} of \"{uploadId}\".");
            ms.Write(storedPart.Body, 0, storedPart.Body.Length);
        }

        _store.Objects[key] = new InMemoryS3StoredObject
        {
            Body = ms.ToArray(),
            LastModified = DateTime.UtcNow,
            ContentType = upload.ContentType,
            UserMetadata = upload.UserMetadata is null
                ? null
                : new Dictionary<string, string>(upload.UserMetadata, StringComparer.OrdinalIgnoreCase),
            StorageClass = upload.StorageClass,
            ServerSideEncryption = upload.ServerSideEncryption
        };
        return Task.CompletedTask;
    }

    public Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _store.Uploads.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    public Task<Uri> GetPreSignedUploadPartUrlAsync(string key, string uploadId, int partNumber, DateTime expiresUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Uri.EscapeDataString(key).Replace("%2F", "/");
        var expires = ((DateTimeOffset)expiresUtc).ToUnixTimeSeconds();
        return Task.FromResult(new Uri(
            $"https://{Bucket}.s3.{Region}.amazonaws.com/{encoded}?uploadId={uploadId}&partNumber={partNumber}&X-Amz-Expires={expires}&X-Amz-Signature=test"));
    }

    public int ActiveMultipartUploadCount => _store.Uploads.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_world is null)
        {
            _store.Objects.Clear();
            _store.Uploads.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InMemoryS3Client));
    }
}
