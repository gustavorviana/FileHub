using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OciMultipartStreamTests
{
    private const int PartSize = 5 * 1024 * 1024;

    private static OracleObjectStorageFileHub NewHub(out InMemoryOciClient client)
    {
        client = new InMemoryOciClient();
        return OracleObjectStorageFileHub.FromOciClient(client);
    }

    [Fact]
    public async Task SmallPayload_CompletesWithSinglePart()
    {
        using var hub = NewHub(out var client);
        var file = (OracleObjectStorageFile)hub.Root.CreateFile("small.bin");

        var payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        var options = new OciWriteOptions
        {
            Multipart = new MultipartStreamOptions(PartSize, PartSize),
            StreamPreference = WriteStreamPreference.Multipart,
        };
        using (var stream = await file.GetWriteStreamAsync(options))
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        Assert.True(client.TryGetBody("small.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task LargePayload_UploadsMultipleParts()
    {
        using var hub = NewHub(out var client);
        var file = (OracleObjectStorageFile)hub.Root.CreateFile("big.bin");

        // 12 MiB → 2 full parts + 1 tail part.
        var payload = new byte[PartSize * 2 + 1024];
        for (long i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 31) & 0xFF);

        var options = new OciWriteOptions
        {
            Multipart = new MultipartStreamOptions(PartSize, PartSize),
            StreamPreference = WriteStreamPreference.Multipart,
        };
        using (var stream = await file.GetWriteStreamAsync(options))
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        Assert.True(client.TryGetBody("big.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task RegularWriteStream_LargePayload_SpillsToMultipart()
    {
        using var hub = NewHub(out var client);
        var file = hub.Root.CreateFile("spill.bin");

        // Per-write policy keeps this test small while proving threshold and
        // part size are resolved together from MultipartStreamOptions.
        var payload = new byte[PartSize * 2 + 1024];
        for (long i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 17) & 0xFF);

        var options = new OciWriteOptions { Multipart = new MultipartStreamOptions(PartSize, PartSize) };
        using (var stream = await file.GetWriteStreamAsync(options))
        {
            // 80 KiB chunks, like FileEntry's copy helpers.
            var chunk = 81920;
            for (int off = 0; off < payload.Length; off += chunk)
                await stream.WriteAsync(payload, off, Math.Min(chunk, payload.Length - off));
        }

        Assert.True(client.TryGetBody("spill.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
        Assert.Equal(payload.Length, file.Length);
    }

    [Fact]
    public async Task RegularWriteStream_SmallPayload_StaysSinglePut()
    {
        using var hub = NewHub(out var client);
        var file = hub.Root.CreateFile("no-spill.bin");
        var putsBefore = client.PutInvocationCount;

        var payload = new byte[1024];
        using (var stream = await file.GetWriteStreamAsync())
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        Assert.True(client.TryGetBody("no-spill.bin", out var body));
        Assert.Equal(payload, body);
        // Single PutObject, no multipart machinery for small payloads.
        Assert.Equal(putsBefore + 1, client.PutInvocationCount);
    }

    [Fact]
    public async Task ExceptionDuringWrite_AbortsUpload()
    {
        using var hub = NewHub(out var client);
        var file = (OracleObjectStorageFile)hub.Root.CreateFile("will-fail.bin");
        // CreateFile materializes an empty object; remove it so this test isolates
        // the multipart-only path.
        file.Delete();

        var firstPart = new byte[PartSize]; // exactly one part
        var stream = await file.GetWriteStreamAsync(
            new OciWriteOptions { StreamPreference = WriteStreamPreference.Multipart });
        try
        {
            await stream.WriteAsync(firstPart, 0, firstPart.Length); // triggers UploadPart
            // Now break the stream by passing a bad count.
            await Assert.ThrowsAsync<ArgumentException>(() => stream.WriteAsync(firstPart, 0, firstPart.Length + 1));
        }
        finally
        {
            // Dispose after the exception — should not commit.
            stream.Dispose();
        }

        Assert.False(client.TryGetBody("will-fail.bin", out _));
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public void HubMultipartDefaults_Are32MibThresholdAnd64MibParts()
    {
        var multipart = new OracleObjectStorageHubOptions().Multipart;
        Assert.Equal(32L * 1024 * 1024, multipart.Threshold);
        Assert.Equal(64L * 1024 * 1024, multipart.PartSize);
    }

    [Fact]
    public async Task Multipart_AppliesWriteOptions()
    {
        using var hub = NewHub(out _);
        var file = (OracleObjectStorageFile)hub.Root.CreateFile("img.bin");

        var options = new FileWriteOptions
        {
            ContentType = "image/png",
            CacheControl = "public,max-age=3600",
            Metadata = new Dictionary<string, string> { ["owner"] = "team-x" },
            StreamPreference = WriteStreamPreference.Multipart,
        };

        var payload = new byte[1024];
        using (var stream = await file.GetWriteStreamAsync(options))
            await stream.WriteAsync(payload, 0, payload.Length);

        var meta = await hub.Root.OpenFile("img.bin").GetMetadataAsync();
        Assert.Equal("image/png", meta.ContentType);
        Assert.Equal("public,max-age=3600", meta.CacheControl);
        Assert.Equal("team-x", meta.Tags["owner"]);
    }

    [Fact]
    public async Task PreferenceMultipart_SmallPayload_SkipsBufferingPhase()
    {
        using var hub = NewHub(out var client);
        var file = hub.Root.CreateFile("prefer-mp.bin");
        var putsBefore = client.PutInvocationCount;

        // 1 KiB — way under the 32 MiB default threshold, but the caller asked
        // for multipart up-front, so no PutObject fires for the content.
        var payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        using (var stream = await file.GetWriteStreamAsync(
            new OciWriteOptions { StreamPreference = WriteStreamPreference.Multipart }))
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        Assert.True(client.TryGetBody("prefer-mp.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(putsBefore, client.PutInvocationCount);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task SetBytesAsync_UsesPreferenceFromWriteOptions()
    {
        using var hub = NewHub(out var client);
        var file = hub.Root.CreateFile("set-bytes-multipart.bin");
        var putsBefore = client.PutInvocationCount;
        var payload = new byte[1024];
        var options = new OciWriteOptions
        {
            StreamPreference = WriteStreamPreference.Multipart,
            Multipart = new MultipartStreamOptions(PartSize, PartSize),
        };

        await file.SetBytesAsync(payload, options);

        Assert.True(client.TryGetBody("set-bytes-multipart.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(putsBefore, client.PutInvocationCount);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task PreferenceSingle_LargePayload_NeverSpills()
    {
        using var hub = NewHub(out var client);
        var file = hub.Root.CreateFile("prefer-single.bin");
        var putsBefore = client.PutInvocationCount;

        // Single forbids the spill regardless of the configured threshold:
        // whole payload buffers and commits as one PutObject.
        var payload = new byte[PartSize + 1024 * 1024];
        for (long i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 13) & 0xFF);

        using (var stream = await file.GetWriteStreamAsync(
            new OciWriteOptions { StreamPreference = WriteStreamPreference.Single }))
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        Assert.True(client.TryGetBody("prefer-single.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(putsBefore + 1, client.PutInvocationCount);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task Multipart_EmptyPayload_StillAppliesWriteOptions()
    {
        using var hub = NewHub(out var client);
        var file = (OracleObjectStorageFile)hub.Root.CreateFile("empty.bin");

        var options = new FileWriteOptions
        {
            ContentType = "text/plain",
            StreamPreference = WriteStreamPreference.Multipart,
        };
        using (await file.GetWriteStreamAsync(options))
        {
            // no write → zero-byte completion path
        }

        var meta = await hub.Root.OpenFile("empty.bin").GetMetadataAsync();
        Assert.Equal("text/plain", meta.ContentType);
        Assert.True(client.TryGetBody("empty.bin", out var body));
        Assert.Empty(body);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }
}
