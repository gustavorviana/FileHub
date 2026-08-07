using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3MetadataTests
{
    private static AmazonS3FileHub NewHub(out InMemoryS3Client client)
    {
        client = new InMemoryS3Client();
        return AmazonS3FileHub.FromS3Client(client);
    }

    [Fact]
    public async Task GetMetadata_RoundTripsContentTypeUserMetadataAndTypedFields()
    {
        using var hub = NewHub(out _);
        var file = hub.Root.CreateFile("img.bin");

        await file.SetBytesAsync(
            new byte[] { 1, 2, 3 },
            new S3WriteOptions
            {
                ContentType = "image/png",
                CacheControl = "public,max-age=3600",
                StorageClass = "GLACIER",
                ServerSideEncryption = "AES256",
                Metadata = new Dictionary<string, string> { ["owner"] = "team-x" },
            });

        var meta = (AmazonS3FileMetadata)await hub.Root.OpenFile("img.bin").GetMetadataAsync();

        Assert.Equal("image/png", meta.ContentType);
        Assert.Equal("public,max-age=3600", meta.CacheControl);
        Assert.Equal("GLACIER", meta.StorageClass);
        Assert.Equal("AES256", meta.ServerSideEncryption);
        Assert.Equal("team-x", meta.Tags["owner"]);
    }

    // N5 — GetMetadataAsync must hand back an independent snapshot, not the
    // file's live cached instance: a later write must not mutate a snapshot a
    // caller is still holding.
    [Fact]
    public async Task GetMetadata_ReturnsIndependentSnapshot()
    {
        using var hub = NewHub(out _);
        var file = hub.Root.CreateFile("x.bin");

        await file.SetBytesAsync(
            new byte[] { 1 },
            new S3WriteOptions
            {
                ContentType = "image/png",
                Metadata = new Dictionary<string, string> { ["k"] = "v1" },
            });
        var snapshot = await file.GetMetadataAsync();

        await file.SetBytesAsync(
            new byte[] { 2 },
            new S3WriteOptions
            {
                ContentType = "text/plain",
                Metadata = new Dictionary<string, string> { ["k"] = "v2" },
            });

        Assert.Equal("image/png", snapshot.ContentType);
        Assert.Equal("v1", snapshot.Tags["k"]);
    }

    // Parity with OCI: options live with the write stream, so opening a write
    // stream with options and abandoning it must not leak into the next write.
    [Fact]
    public async Task WriteOptions_AbandonedStream_DoNotLeakIntoNextWrite()
    {
        using var hub = NewHub(out _);
        var file = hub.Root.CreateFile("a.bin");

        using (file.GetWriteStream(new S3WriteOptions { ContentType = "image/png" }))
        {
            // no write → nothing flushed → nothing committed
        }

        await file.SetBytesAsync(new byte[] { 9 });

        var meta = await hub.Root.OpenFile("a.bin").GetMetadataAsync();
        Assert.Null(meta.ContentType);
    }
}
