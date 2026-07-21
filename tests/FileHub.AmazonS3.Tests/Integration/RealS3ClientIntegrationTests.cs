using System;
using System.Text;

namespace FileHub.AmazonS3.Tests.Integration;

/// <summary>
/// End-to-end round-trip tests against real AWS S3. Opt-in via the
/// environment variables listed in <see cref="AwsEnvironment.RequiredVars"/>.
/// Skipped otherwise so CI passes without AWS credentials.
/// </summary>
public class RealS3ClientIntegrationTests : RealS3IntegrationTestBase
{
    [RequiresAws]
    public async Task UploadDownloadDelete_RoundTrip()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "roundtrip");
        try
        {
            var file = rootDir.CreateFile("integration.txt");
            file.SetText("hello from S3");

            var got = rootDir.OpenFile("integration.txt").ReadAllText();
            Assert.Equal("hello from S3", got);

            rootDir.OpenFile("integration.txt").Delete();
            Assert.False(rootDir.FileExists("integration.txt"));
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresAws]
    public async Task MissingObject_ThrowsFileNotFoundException()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "notfound");
        try
        {
            Assert.Throws<System.IO.FileNotFoundException>(() =>
            {
                _ = rootDir.OpenFile("does-not-exist.bin").ReadAllText();
            });
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresAws]
    public async Task GetSignedUrl_ReturnsDownloadableUrl()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "signed-url");
        try
        {
            var file = rootDir.CreateFile("signed.txt");
            var payload = Encoding.UTF8.GetBytes("signed-content");
            file.SetBytes(payload);

            var url = ((IUrlAccessible)rootDir.OpenFile("signed.txt")).GetSignedUrl(TimeSpan.FromMinutes(5));

            using var http = new System.Net.Http.HttpClient();
            var downloaded = await http.GetByteArrayAsync(url);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresAws]
    public async Task GetSignedUploadUrl_PutsObjectIntoBucket()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "signed-upload");
        try
        {
            var dir = (ISignedUploadable)rootDir;
            var payload = Encoding.UTF8.GetBytes("uploaded-via-presigned-put");

            var url = dir.GetSignedUploadUrl("upload.bin", TimeSpan.FromMinutes(5));

            using var http = new System.Net.Http.HttpClient();
            using var content = new System.Net.Http.ByteArrayContent(payload);
            using var resp = await http.PutAsync(url, content);
            resp.EnsureSuccessStatusCode();

            Assert.True(rootDir.FileExists("upload.bin"));
            Assert.Equal(payload, rootDir.OpenFile("upload.bin").ReadAllBytes());
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresAws]
    public async Task GetSignedUploadUrl_WithOptions_BindsContentTypeAndCacheControl()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "signed-upload-options");
        try
        {
            var dir = (ISignedUploadable)rootDir;
            var payload = Encoding.UTF8.GetBytes("typed");
            var options = new S3WriteOptions
            {
                ContentType = "text/plain",
                CacheControl = "max-age=60",
            };

            var url = dir.GetSignedUploadUrl("upload.txt", TimeSpan.FromMinutes(5), options);

            using var http = new System.Net.Http.HttpClient();
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Put, url);
            var content = new System.Net.Http.ByteArrayContent(payload);
            // Headers MUST match the signature bindings or S3 returns SignatureDoesNotMatch.
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            request.Content = content;
            request.Headers.CacheControl = System.Net.Http.Headers.CacheControlHeaderValue.Parse("max-age=60");
            using var resp = await http.SendAsync(request);
            resp.EnsureSuccessStatusCode();

            var meta = rootDir.OpenFile("upload.txt").GetMetadata();
            Assert.Equal("text/plain", meta.ContentType);
            Assert.Equal("max-age=60", meta.CacheControl);
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresAws]
    public async Task GetSignedUploadUrl_WithOptions_MismatchedHeaders_Rejected()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "signed-upload-mismatch");
        try
        {
            var dir = (ISignedUploadable)rootDir;
            var options = new S3WriteOptions { ContentType = "image/png" };

            var url = dir.GetSignedUploadUrl("upload.txt", TimeSpan.FromMinutes(5), options);

            using var http = new System.Net.Http.HttpClient();
            using var content = new System.Net.Http.ByteArrayContent(Encoding.UTF8.GetBytes("x"));
            // Caller sends a different Content-Type than what was signed → S3 must reject.
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var resp = await http.PutAsync(url, content);

            Assert.Equal(System.Net.HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.False(rootDir.FileExists("upload.txt"));
        }
        finally
        {
            rootDir.Delete();
        }
    }
}
