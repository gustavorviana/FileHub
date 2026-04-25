using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace FileHub.AmazonS3.Tests.Integration;

/// <summary>
/// Opt-in integration tests against real AWS S3 covering the multipart upload
/// paths: both the stream-based (<see cref="IMultipartUploadable"/>) and
/// the presigned-URL (<see cref="IMultipartUploadSignable"/>) flows.
/// Requires the same env vars as <see cref="RealS3ClientIntegrationTests"/>.
/// </summary>
public class RealS3MultipartIntegrationTests
{
    private const string Prefix = "filehub-integration";
    private const int PartSize = 5 * 1024 * 1024; // S3 minimum.

    private static AmazonS3FileHub CreateHub()
    {
        var bucket = Environment.GetEnvironmentVariable("FILEHUB_S3_BUCKET")!;
        var region = Environment.GetEnvironmentVariable("AWS_REGION")!;
        var key = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")!;
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")!;
        var credentials = new Amazon.Runtime.BasicAWSCredentials(key, secret);
        return AmazonS3FileHub.FromCredentials(
            rootPath: Prefix,
            bucketName: bucket,
            credentials: credentials,
            region: region);
    }

    [RequiresAws]
    public async Task MultipartStream_LargeFile_RoundTrip()
    {
        using var hub = CreateHub();
        var name = $"multipart-stream-{Guid.NewGuid():N}.bin";

        // 6 MiB forces exactly 2 parts (one 5 MiB, one 1 MiB tail).
        var payload = new byte[PartSize + 1024 * 1024];
        new Random(1).NextBytes(payload);

        var file = (AmazonS3File)hub.Root.CreateFile(name);
        try
        {
            using (var stream = await file.GetMultipartWriteStreamAsync())
            {
                await stream.WriteAsync(payload, 0, payload.Length);
            }

            var downloaded = hub.Root.OpenFile(name).ReadAllBytes();
            Assert.Equal(payload.Length, downloaded.Length);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            TryDelete(() => hub.Root.OpenFile(name).Delete());
        }
    }

    [RequiresAws]
    public async Task SignedMultipart_PresignedUrls_Work()
    {
        using var hub = CreateHub();
        var name = $"signed-multipart-{Guid.NewGuid():N}.bin";

        // 12 MiB → FromPartSize gives 3 parts (5 + 5 + 2 MiB last).
        const long total = (long)PartSize * 2 + 2 * 1024 * 1024;
        var payload = new byte[total];
        new Random(2).NextBytes(payload);

        var file = (AmazonS3File)hub.Root.CreateFile(name);
        // Remove the empty placeholder so multipart completes without racing
        // against the CreateFile PUT.
        file.Delete();

        var spec = MultipartUploadSpec.FromPartSize(total, PartSize);

        SignedMultipartUpload session = null!;
        try
        {
            session = await file.BeginSignedMultipartUploadAsync(spec, TimeSpan.FromMinutes(30));
            Assert.Equal(3, session.Parts.Count);

            // Upload each part via HttpClient against the presigned URL,
            // capture the ETag header from each response, assemble the list.
            var uploaded = new List<UploadedPart>(session.Parts.Count);
            using var http = new HttpClient();
            long offset = 0;
            foreach (var part in session.Parts)
            {
                var len = part.ContentLength;
                var slice = new byte[len];
                Array.Copy(payload, offset, slice, 0, len);
                offset += len;

                using var content = new ByteArrayContent(slice);
                using var resp = await http.PutAsync(part.UploadUrl, content);
                resp.EnsureSuccessStatusCode();

                var etag = resp.Headers.ETag?.Tag
                    ?? resp.Headers.GetValues("ETag").FirstOrDefault()
                    ?? throw new InvalidOperationException($"No ETag returned for part {part.PartNumber}.");
                uploaded.Add(new UploadedPart(part.PartNumber, etag));
            }

            await file.CompleteSignedMultipartUploadAsync(session.UploadId, uploaded);

            var downloaded = hub.Root.OpenFile(name).ReadAllBytes();
            Assert.Equal(payload.Length, downloaded.Length);
            Assert.Equal(payload, downloaded);
        }
        catch
        {
            if (session != null)
                TryDelete(() => file.AbortSignedMultipartUpload(session.UploadId));
            throw;
        }
        finally
        {
            TryDelete(() => hub.Root.OpenFile(name).Delete());
        }
    }

    private static void TryDelete(Action action)
    {
        try { action(); } catch { /* cleanup best-effort */ }
    }
}
