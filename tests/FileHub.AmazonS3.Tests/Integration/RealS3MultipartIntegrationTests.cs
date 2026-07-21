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
/// paths: both the regular write stream and
/// the presigned-URL (<see cref="IMultipartUploadSignable"/>) flows.
/// Requires the same env vars as <see cref="RealS3ClientIntegrationTests"/>.
/// </summary>
public class RealS3MultipartIntegrationTests : RealS3IntegrationTestBase
{
    private const int PartSize = 5 * 1024 * 1024; // S3 minimum.

    [RequiresAws]
    public async Task MultipartStream_LargeFile_RoundTrip()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "multipart-stream");

        // 6 MiB forces exactly 2 parts (one 5 MiB, one 1 MiB tail).
        var payload = new byte[PartSize + 1024 * 1024];
        new Random(1).NextBytes(payload);

        try
        {
            var file = rootDir.CreateFile("multipart.bin");
            var options = new S3WriteOptions
            {
                Multipart = new MultipartStreamOptions(PartSize, PartSize),
            };
            using (var stream = await file.GetWriteStreamAsync(options, WriteStreamPreference.Multipart))
            {
                await stream.WriteAsync(payload, 0, payload.Length);
            }

            var downloaded = rootDir.OpenFile("multipart.bin").ReadAllBytes();
            Assert.Equal(payload.Length, downloaded.Length);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            TryDelete(rootDir.Delete);
        }
    }

    [RequiresAws]
    public async Task SignedMultipart_PresignedUrls_Work()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "signed-multipart");

        // 12 MiB → FromPartSize gives 3 parts (5 + 5 + 2 MiB last).
        const long total = (long)PartSize * 2 + 2 * 1024 * 1024;
        var payload = new byte[total];
        new Random(2).NextBytes(payload);

        var file = (AmazonS3File)rootDir.CreateFile("multipart.bin");
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

            var downloaded = rootDir.OpenFile("multipart.bin").ReadAllBytes();
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
            TryDelete(rootDir.Delete);
        }
    }

    private static void TryDelete(Action action)
    {
        try { action(); } catch { /* cleanup best-effort */ }
    }
}
