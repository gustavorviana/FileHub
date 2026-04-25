using System;
using System.IO;
using System.Threading.Tasks;
using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3MultipartStreamTests
{
    private const int PartSize = 5 * 1024 * 1024;

    private static AmazonS3FileHub NewHub(out InMemoryS3Client client)
    {
        client = new InMemoryS3Client();
        return AmazonS3FileHub.FromS3Client(client);
    }

    [Fact]
    public async Task SmallPayload_CompletesWithSinglePart()
    {
        using var hub = NewHub(out var client);
        var file = (AmazonS3File)hub.Root.CreateFile("small.bin");

        var payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        using (var stream = await file.GetMultipartWriteStreamAsync())
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
        var file = (AmazonS3File)hub.Root.CreateFile("big.bin");

        // 12 MiB → 2 full parts + 1 tail part.
        var payload = new byte[PartSize * 2 + 1024];
        for (long i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 31) & 0xFF);

        using (var stream = await file.GetMultipartWriteStreamAsync())
        {
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        Assert.True(client.TryGetBody("big.bin", out var body));
        Assert.Equal(payload, body);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task ExceptionDuringWrite_AbortsUpload()
    {
        using var hub = NewHub(out var client);
        var file = (AmazonS3File)hub.Root.CreateFile("will-fail.bin");
        // CreateFile materializes an empty object; remove it so this test isolates
        // the multipart-only path.
        file.Delete();

        var firstPart = new byte[PartSize]; // exactly one part
        var stream = await file.GetMultipartWriteStreamAsync();
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
    public async Task MinimumPartSize_Is5Mib()
    {
        using var hub = NewHub(out _);
        var file = (AmazonS3File)hub.Root.CreateFile("x.bin");
        IMultipartUploadable up = file;
        Assert.Equal(PartSize, up.MinimumPartSize);
        await Task.CompletedTask;
    }
}
