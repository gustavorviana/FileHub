using System.Collections.Concurrent;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage.Tests.Fakes;

/// <summary>
/// Test-side analogue of a single authenticated <c>ObjectStorageClient</c>:
/// a pool of bucket stores keyed by <c>(namespace, bucket)</c> shared by
/// every <see cref="InMemoryOciClient"/> created from this world. Clients
/// built from the same world share <see cref="IOciClient.CredentialScope"/>
/// and therefore exercise the server-side cross-target copy path in the
/// driver; clients built without a world have independent scopes and fall
/// back to generic stream copy.
/// </summary>
internal sealed class InMemoryOciWorld
{
    private readonly ConcurrentDictionary<(string Namespace, string Bucket), InMemoryBucketStore> _buckets
        = new();

    public InMemoryOciClient CreateClient(
        string @namespace = "test-ns",
        string bucket = "test-bucket",
        string region = "us-test-1")
        => InMemoryOciClient.InWorld(this, @namespace, bucket, region);

    internal InMemoryBucketStore GetOrCreateStore(string @namespace, string bucket)
        => _buckets.GetOrAdd((@namespace, bucket), _ => new InMemoryBucketStore());
}

internal sealed class InMemoryBucketStore
{
    public ConcurrentDictionary<string, InMemoryStoredObject> Objects { get; }
        = new(System.StringComparer.Ordinal);

    public ConcurrentDictionary<string, InMemoryOciClient.ParRecord> Pars { get; }
        = new(System.StringComparer.Ordinal);

    public OciBucketAccessType BucketAccess { get; set; } = OciBucketAccessType.NoPublicAccess;
}

internal sealed class InMemoryStoredObject
{
    public byte[] Body { get; set; } = System.Array.Empty<byte>();
    public string? ContentType { get; set; }
    public System.Collections.Generic.Dictionary<string, string>? OpcMeta { get; set; }
    public System.DateTime LastModified { get; set; }
}
