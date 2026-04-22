using System;
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
/// <remarks>
/// Two construction modes:
/// <list type="bullet">
///   <item>Stand-alone: <c>new InMemoryOciClient(...)</c> — each instance
///   owns its bucket store and has a distinct <see cref="CredentialScope"/>.
///   Matches the "two independent FileHubs" scenario, where the driver must
///   fall back to stream copy.</item>
///   <item>World-backed: <see cref="InWorld"/> — the client borrows a bucket
///   store from a shared <see cref="InMemoryOciWorld"/>, and all clients in
///   the same world share their <see cref="CredentialScope"/>. Cross-bucket
///   <see cref="CopyObjectAsync"/> writes to the destination's store in the
///   same world, exercising the server-side path.</item>
/// </list>
/// </remarks>
internal sealed class InMemoryOciClient : IOciClient
{
    private readonly InMemoryOciWorld? _world;
    private readonly object _ownScope;
    private readonly InMemoryBucketStore _store;
    private bool _disposed;
    private int _copyInvocationCount;

    public string Namespace { get; }
    public string Bucket { get; }
    public string Region { get; }

    public object CredentialScope => (object?)_world ?? _ownScope;

    /// <summary>Number of times <see cref="CopyObjectAsync"/> was invoked on this client instance.</summary>
    public int CopyInvocationCount => _copyInvocationCount;

    public InMemoryOciClient(string bucket = "test-bucket", string @namespace = "test-ns", string region = "us-test-1")
    {
        _world = null;
        _ownScope = new object();
        _store = new InMemoryBucketStore();
        Bucket = bucket;
        Namespace = @namespace;
        Region = region;
    }

    private InMemoryOciClient(InMemoryOciWorld world, string @namespace, string bucket, string region)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _ownScope = null!; // unused when world is set
        _store = world.GetOrCreateStore(@namespace, bucket);
        Namespace = @namespace;
        Bucket = bucket;
        Region = region;
    }

    internal static InMemoryOciClient InWorld(InMemoryOciWorld world, string @namespace, string bucket, string region)
        => new(world, @namespace, bucket, region);

    public void SetBucketAccess(OciBucketAccessType access) => _store.BucketAccess = access;

    public int ObjectCount => _store.Objects.Count;
    public IReadOnlyCollection<string> Keys => _store.Objects.Keys.ToArray();
    public IReadOnlyCollection<ParRecord> Pars => _store.Pars.Values.ToArray();

    public bool TryGetBody(string objectName, out byte[] body)
    {
        if (_store.Objects.TryGetValue(objectName, out var obj))
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
        if (!_store.Objects.TryGetValue(objectName, out var obj))
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
        if (!_store.Objects.TryGetValue(objectName, out var obj))
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

        var stored = new InMemoryStoredObject
        {
            Body = bytes,
            ContentType = contentType,
            OpcMeta = opcMeta is null
                ? null
                : new Dictionary<string, string>(opcMeta, StringComparer.OrdinalIgnoreCase),
            LastModified = DateTime.UtcNow
        };

        _store.Objects[objectName] = stored;
    }

    public Task DeleteObjectAsync(string objectName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.Objects.TryRemove(objectName, out _))
            throw new FileNotFoundException($"Object \"{objectName}\" not found.");
        return Task.CompletedTask;
    }

    public Task RenameObjectAsync(string sourceName, string newName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.Objects.TryRemove(sourceName, out var obj))
            throw new FileNotFoundException($"Object \"{sourceName}\" not found.");

        _store.Objects[newName] = obj;
        return Task.CompletedTask;
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
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _copyInvocationCount);

        if (!_store.Objects.TryGetValue(sourceObjectName, out var obj))
            throw new FileNotFoundException($"Object \"{sourceObjectName}\" not found.");

        InMemoryBucketStore destStore;
        bool sameTarget =
            string.Equals(destinationNamespace, Namespace, StringComparison.Ordinal)
            && string.Equals(destinationBucket, Bucket, StringComparison.Ordinal)
            && string.Equals(destinationRegion, Region, StringComparison.Ordinal);

        if (sameTarget)
        {
            destStore = _store;
        }
        else if (_world is not null)
        {
            destStore = _world.GetOrCreateStore(destinationNamespace, destinationBucket);
        }
        else
        {
            throw new InvalidOperationException(
                "Cross-target CopyObject on a scope-less InMemoryOciClient — the driver should have fallen back to stream copy.");
        }

        destStore.Objects[destinationObjectName] = new InMemoryStoredObject
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
        var orderedKeys = _store.Objects.Keys
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

            _store.Objects.TryGetValue(key, out var obj);
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
        return Task.FromResult(new OciBucketInfo { PublicAccessType = _store.BucketAccess });
    }

    public Task<string> CreatePreauthenticatedReadRequestAsync(string objectName, string parName, DateTime timeExpiresUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.Objects.ContainsKey(objectName))
            throw new FileNotFoundException($"Object \"{objectName}\" not found.");

        var accessUri = $"/p/{parName}/n/{Namespace}/b/{Bucket}/o/{Uri.EscapeDataString(objectName)}";
        _store.Pars[parName] = new ParRecord(parName, objectName, timeExpiresUtc, accessUri);
        return Task.FromResult(accessUri);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // When world-backed, the store lives on the world and must not be cleared.
        if (_world is null)
        {
            _store.Objects.Clear();
            _store.Pars.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InMemoryOciClient));
    }

    public sealed record ParRecord(string Name, string ObjectName, DateTime TimeExpiresUtc, string AccessUri);
}
