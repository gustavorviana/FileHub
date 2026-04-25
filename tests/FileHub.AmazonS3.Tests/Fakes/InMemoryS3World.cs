using System.Collections.Concurrent;

namespace FileHub.AmazonS3.Tests.Fakes;

/// <summary>
/// Test-side analogue of a single authenticated <c>IAmazonS3</c> client: a
/// pool of bucket stores shared by every <see cref="InMemoryS3Client"/>
/// created from this world. Clients built from the same world share
/// <c>CredentialScope</c> and therefore exercise the server-side
/// cross-bucket copy path in the driver; clients built without a world
/// have independent scopes and fall back to generic stream copy.
/// </summary>
internal sealed class InMemoryS3World
{
    private readonly ConcurrentDictionary<string, InMemoryS3BucketStore> _buckets = new();

    public InMemoryS3Client CreateClient(
        string bucket = "test-bucket",
        string region = "us-test-1")
        => InMemoryS3Client.InWorld(this, bucket, region);

    internal InMemoryS3BucketStore GetOrCreateStore(string bucket)
        => _buckets.GetOrAdd(bucket, _ => new InMemoryS3BucketStore());
}

internal sealed class InMemoryS3BucketStore
{
    public ConcurrentDictionary<string, InMemoryS3StoredObject> Objects { get; } = new(System.StringComparer.Ordinal);
    public ConcurrentDictionary<string, InMemoryS3MultipartUpload> Uploads { get; } = new(System.StringComparer.Ordinal);
    public bool IsPublic { get; set; }
}

internal sealed class InMemoryS3StoredObject
{
    public byte[] Body { get; set; } = System.Array.Empty<byte>();
    public string? ContentType { get; set; }
    public System.Collections.Generic.Dictionary<string, string>? UserMetadata { get; set; }
    public System.DateTime LastModified { get; set; }
    public string? StorageClass { get; set; }
    public string? ServerSideEncryption { get; set; }
}

internal sealed class InMemoryS3MultipartUpload
{
    public string UploadId { get; init; } = "";
    public string Key { get; init; } = "";
    public string? ContentType { get; init; }
    public System.Collections.Generic.Dictionary<string, string>? UserMetadata { get; init; }
    public string? StorageClass { get; init; }
    public string? ServerSideEncryption { get; init; }
    public ConcurrentDictionary<int, InMemoryS3UploadedPart> Parts { get; } = new();
}

internal sealed class InMemoryS3UploadedPart
{
    public byte[] Body { get; init; } = System.Array.Empty<byte>();
    public string ETag { get; init; } = "";
}
