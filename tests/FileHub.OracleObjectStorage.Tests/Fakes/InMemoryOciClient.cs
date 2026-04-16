using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage.Tests.Fakes;

/// <summary>
/// Deterministic in-memory <see cref="IOciClient"/> implementation that lets
/// the driver logic be unit-tested without contacting OCI. Semantics mirror
/// the OCI API as described in its docs, including common-prefix enumeration
/// with delimiter and simple cursor-based paging.
/// </summary>
internal sealed class InMemoryOciClient : IOciClient
{
    private readonly ConcurrentDictionary<string, StoredObject> _store = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ParRecord> _pars = new(StringComparer.Ordinal);
    private OciBucketAccessType _bucketAccess = OciBucketAccessType.NoPublicAccess;
    private bool _disposed;

    public string Namespace { get; }
    public string Bucket { get; }
    public string Region { get; }

    public InMemoryOciClient(string bucket = "test-bucket", string @namespace = "test-ns", string region = "us-test-1")
    {
        Bucket = bucket;
        Namespace = @namespace;
        Region = region;
    }

    public void SetBucketAccess(OciBucketAccessType access) => _bucketAccess = access;

    public int ObjectCount => _store.Count;
    public IReadOnlyCollection<string> Keys => _store.Keys.ToArray();
    public IReadOnlyCollection<ParRecord> Pars => _pars.Values.ToArray();

    public bool TryGetBody(string objectName, out byte[] body)
    {
        if (_store.TryGetValue(objectName, out var obj))
        {
            body = obj.Body;
            return true;
        }
        body = null!;
        return false;
    }

    public Task<OciHeadResult> HeadObjectAsync(string objectName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryGetValue(objectName, out var obj))
            throw new FileNotFoundException($"Object \"{objectName}\" not found.");

        return Task.FromResult(new OciHeadResult
        {
            ContentLength = obj.Body.LongLength,
            LastModified = obj.LastModified,
            ContentType = obj.ContentType,
            OpcMeta = obj.OpcMeta is null
                ? null
                : new Dictionary<string, string>(obj.OpcMeta, StringComparer.OrdinalIgnoreCase)
        });
    }

    public Task<OciGetResult> GetObjectAsync(string objectName, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryGetValue(objectName, out var obj))
            throw new FileNotFoundException($"Object \"{objectName}\" not found.");

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

        return Task.FromResult(new OciGetResult { InputStream = new MemoryStream(slice, writable: false) });
    }

    public async Task PutObjectAsync(
        string objectName,
        Stream body,
        long contentLength,
        string contentType,
        IReadOnlyDictionary<string, string> opcMeta,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

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

        var stored = new StoredObject
        {
            Body = bytes,
            ContentType = contentType,
            OpcMeta = opcMeta is null
                ? null
                : new Dictionary<string, string>(opcMeta, StringComparer.OrdinalIgnoreCase),
            LastModified = DateTime.UtcNow
        };

        _store[objectName] = stored;
    }

    public Task DeleteObjectAsync(string objectName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryRemove(objectName, out _))
            throw new FileNotFoundException($"Object \"{objectName}\" not found.");
        return Task.CompletedTask;
    }

    public Task RenameObjectAsync(string sourceName, string newName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryRemove(sourceName, out var obj))
            throw new FileNotFoundException($"Object \"{sourceName}\" not found.");

        _store[newName] = obj;
        return Task.CompletedTask;
    }

    public Task CopyObjectAsync(string sourceObjectName, string destinationObjectName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryGetValue(sourceObjectName, out var obj))
            throw new FileNotFoundException($"Object \"{sourceObjectName}\" not found.");

        _store[destinationObjectName] = new StoredObject
        {
            Body = (byte[])obj.Body.Clone(),
            ContentType = obj.ContentType,
            OpcMeta = obj.OpcMeta is null ? null : new Dictionary<string, string>(obj.OpcMeta, StringComparer.OrdinalIgnoreCase),
            LastModified = DateTime.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task<OciListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string start, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        prefix ??= string.Empty;
        var orderedKeys = _store.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Where(k => string.IsNullOrEmpty(start) || string.CompareOrdinal(k, start) >= 0)
            .OrderBy(k => k, StringComparer.Ordinal);

        var page = new OciListPage();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        string? lastKey = null;

        foreach (var key in orderedKeys)
        {
            if (limit.HasValue && count >= limit.Value)
            {
                page.NextStartWith = key;
                break;
            }

            if (!string.IsNullOrEmpty(delimiter))
            {
                var rest = key.Substring(prefix.Length);
                int delimIdx = rest.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimIdx >= 0)
                {
                    // Any key with a delimiter after the prefix is grouped into a
                    // common prefix — including marker-only "directory" keys whose
                    // rest is "sub/". Matches S3/OCI ListObjects semantics.
                    var commonPrefix = prefix + rest.Substring(0, delimIdx + delimiter.Length);
                    if (seenPrefixes.Add(commonPrefix))
                    {
                        page.Prefixes.Add(commonPrefix);
                        count++;
                        lastKey = key;
                    }
                    continue;
                }
            }

            _store.TryGetValue(key, out var obj);
            page.Objects.Add(new OciListObject
            {
                Name = key,
                Size = obj?.Body.LongLength ?? 0,
                TimeCreated = obj?.LastModified
            });
            count++;
            lastKey = key;
        }

        return Task.FromResult(page);
    }

    public Task<OciBucketInfo> GetBucketAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OciBucketInfo { PublicAccessType = _bucketAccess });
    }

    public Task<string> CreatePreauthenticatedReadRequestAsync(string objectName, string parName, DateTime timeExpiresUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.ContainsKey(objectName))
            throw new FileNotFoundException($"Object \"{objectName}\" not found.");

        var accessUri = $"/p/{parName}/n/{Namespace}/b/{Bucket}/o/{Uri.EscapeDataString(objectName)}";
        _pars[parName] = new ParRecord(parName, objectName, timeExpiresUtc, accessUri);
        return Task.FromResult(accessUri);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Clear();
        _pars.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InMemoryOciClient));
    }

    public sealed record ParRecord(string Name, string ObjectName, DateTime TimeExpiresUtc, string AccessUri);

    private sealed class StoredObject
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string? ContentType { get; set; }
        public Dictionary<string, string>? OpcMeta { get; set; }
        public DateTime LastModified { get; set; }
    }
}
