using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FileHub.AmazonS3.Tests.Fakes;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3SignedMultipartTests
{
    private const int MinPart = 5 * 1024 * 1024;

    private static AmazonS3FileHub NewHub(out InMemoryS3Client client)
    {
        client = new InMemoryS3Client();
        return AmazonS3FileHub.FromS3Client(client);
    }

    [Fact]
    public async Task Begin_Returns_One_SignedPart_Per_Part()
    {
        using var hub = NewHub(out _);
        var file = (AmazonS3File)hub.Root.CreateFile("doc.bin");

        var spec = MultipartUploadSpec.FromPartSize(totalBytes: MinPart * 3 + 100, partSize: MinPart);
        var session = await file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromHours(1));

        Assert.Equal(4, session.Parts.Count);
        Assert.Equal(spec, session.Spec);
        Assert.NotEmpty(session.UploadId);

        var urls = new HashSet<string>();
        long sum = 0;
        for (int i = 0; i < session.Parts.Count; i++)
        {
            var p = session.Parts[i];
            Assert.Equal(i + 1, p.PartNumber);
            Assert.True(urls.Add(p.UploadUrl.ToString()), "URLs must be distinct");
            sum += p.ContentLength;
        }
        Assert.Equal(spec.TotalBytes, sum);
    }

    [Fact]
    public async Task Complete_AfterPartUploads_MaterializesObject()
    {
        using var hub = NewHub(out var client);
        var file = (AmazonS3File)hub.Root.CreateFile("assembled.bin");

        var spec = MultipartUploadSpec.FromPartSize(totalBytes: MinPart * 2 + 10, partSize: MinPart);
        var session = await file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromHours(1));

        // Simulate the remote client uploading each part via the fake's direct API
        var uploaded = new List<UploadedPart>();
        var rng = new Random(42);
        long offset = 0;
        byte[] fullBuffer = new byte[spec.TotalBytes];
        rng.NextBytes(fullBuffer);
        foreach (var part in session.Parts)
        {
            var len = part.ContentLength;
            using var ms = new MemoryStream(fullBuffer, (int)offset, (int)len, writable: false);
            var etag = await client.UploadPartAsync(file.ObjectKey, session.UploadId, part.PartNumber, ms, len);
            uploaded.Add(new UploadedPart(part.PartNumber, etag));
            offset += len;
        }

        await file.CompleteSignedMultipartUploadAsync(session.UploadId, uploaded);

        Assert.True(client.TryGetBody("assembled.bin", out var body));
        Assert.Equal(fullBuffer, body);
        Assert.Equal(0, client.ActiveMultipartUploadCount);
    }

    [Fact]
    public async Task Abort_DiscardsUpload()
    {
        using var hub = NewHub(out var client);
        var file = (AmazonS3File)hub.Root.CreateFile("aborted.bin");
        file.Delete(); // isolate multipart-only path from the empty CreateFile PUT

        var spec = MultipartUploadSpec.FromPartSize(totalBytes: MinPart, partSize: MinPart);
        var session = await file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromHours(1));

        await file.AbortSignedMultipartUploadAsync(session.UploadId);

        Assert.Equal(0, client.ActiveMultipartUploadCount);
        Assert.False(client.TryGetBody("aborted.bin", out _));
    }

    [Fact]
    public async Task Spec_IntermediatePartBelowMinimum_Throws()
    {
        using var hub = NewHub(out _);
        var file = (AmazonS3File)hub.Root.CreateFile("bad.bin");

        // PartSize < 5 MiB and PartCount > 1 should throw.
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 10_000_000, partSize: 1_000_000);

        await Assert.ThrowsAsync<ArgumentException>(
            () => file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task Spec_TooManyParts_Throws()
    {
        using var hub = NewHub(out _);
        var file = (AmazonS3File)hub.Root.CreateFile("bad.bin");

        var spec = MultipartUploadSpec.FromPartCount(totalBytes: 100_000_000, partCount: 20_000);

        await Assert.ThrowsAsync<ArgumentException>(
            () => file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task Spec_SinglePartBelowMinimum_Allowed()
    {
        using var hub = NewHub(out _);
        var file = (AmazonS3File)hub.Root.CreateFile("tiny.bin");

        // Single part can be smaller than 5 MiB — it's the "last and only" part.
        var spec = MultipartUploadSpec.FromPartSize(totalBytes: 1_000, partSize: 1_000);

        var session = await file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromMinutes(10));

        Assert.Single(session.Parts);
        Assert.Equal(1_000, session.Parts[0].ContentLength);

        // Cleanup
        await file.AbortSignedMultipartUploadAsync(session.UploadId);
    }
}
