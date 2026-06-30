using System;
using System.Text;

namespace FileHub.AmazonS3.Tests.Integration;

/// <summary>
/// End-to-end round-trip tests against real AWS S3. Opt-in via the
/// environment variables listed in <see cref="AwsEnvironment.RequiredVars"/>.
/// Skipped otherwise so CI passes without AWS credentials.
/// </summary>
public class RealS3ClientIntegrationTests
{
    private static AmazonS3FileHub CreateHub()
    {
        var bucket = Environment.GetEnvironmentVariable("FILEHUB_S3_BUCKET")!;
        var region = Environment.GetEnvironmentVariable("AWS_REGION")!;
        var key = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")!;
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")!;
        var credentials = new Amazon.Runtime.BasicAWSCredentials(key, secret);
        return AmazonS3FileHub.Create(
            S3HubOptions.FromCredentials(bucket, credentials, region, rootPath: "filehub-integration"));
    }

    [RequiresAws]
    public void UploadDownloadDelete_RoundTrip()
    {
        using var hub = CreateHub();
        var name = $"integration-{Guid.NewGuid():N}.txt";
        var file = hub.Root.CreateFile(name);
        file.SetText("hello from S3");

        var got = hub.Root.OpenFile(name).ReadAllText();
        Assert.Equal("hello from S3", got);

        hub.Root.OpenFile(name).Delete();
        Assert.False(hub.Root.FileExists(name));
    }

    [RequiresAws]
    public void MissingObject_ThrowsFileNotFoundException()
    {
        using var hub = CreateHub();
        var name = $"missing-{Guid.NewGuid():N}.bin";

        Assert.Throws<System.IO.FileNotFoundException>(() =>
        {
            _ = hub.Root.OpenFile(name).ReadAllText();
        });
    }

    [RequiresAws]
    public void GetSignedUrl_ReturnsDownloadableUrl()
    {
        using var hub = CreateHub();
        var name = $"signed-{Guid.NewGuid():N}.txt";
        var file = hub.Root.CreateFile(name);
        var payload = Encoding.UTF8.GetBytes("signed-content");
        file.SetBytes(payload);

        var url = ((IUrlAccessible)hub.Root.OpenFile(name)).GetSignedUrl(TimeSpan.FromMinutes(5));

        using var http = new System.Net.Http.HttpClient();
        var downloaded = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        Assert.Equal(payload, downloaded);

        hub.Root.OpenFile(name).Delete();
    }

    [RequiresAws]
    public void GetSignedUploadUrl_PutsObjectIntoBucket()
    {
        using var hub = CreateHub();
        var dir = (ISignedUploadable)hub.Root;
        var name = $"signed-upload-{Guid.NewGuid():N}.bin";
        var payload = Encoding.UTF8.GetBytes("uploaded-via-presigned-put");

        var url = dir.GetSignedUploadUrl(name, TimeSpan.FromMinutes(5));

        using (var http = new System.Net.Http.HttpClient())
        using (var content = new System.Net.Http.ByteArrayContent(payload))
        {
            var resp = http.PutAsync(url, content).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
        }

        Assert.True(hub.Root.FileExists(name));
        var roundTrip = hub.Root.OpenFile(name).ReadAllBytes();
        Assert.Equal(payload, roundTrip);

        hub.Root.OpenFile(name).Delete();
    }

    [RequiresAws]
    public void GetSignedUploadUrl_WithOptions_BindsContentTypeAndCacheControl()
    {
        using var hub = CreateHub();
        var dir = (ISignedUploadable)hub.Root;
        var name = $"signed-upload-opts-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("typed");
        var options = new S3WriteOptions
        {
            ContentType = "text/plain",
            CacheControl = "max-age=60",
        };

        var url = dir.GetSignedUploadUrl(name, TimeSpan.FromMinutes(5), options);

        using (var http = new System.Net.Http.HttpClient())
        using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Put, url))
        {
            var content = new System.Net.Http.ByteArrayContent(payload);
            // Headers MUST match the signature bindings or S3 returns SignatureDoesNotMatch.
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            request.Content = content;
            request.Headers.CacheControl = System.Net.Http.Headers.CacheControlHeaderValue.Parse("max-age=60");
            var resp = http.SendAsync(request).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
        }

        var meta = hub.Root.OpenFile(name).GetMetadata();
        Assert.Equal("text/plain", meta.ContentType);
        Assert.Equal("max-age=60", meta.CacheControl);

        hub.Root.OpenFile(name).Delete();
    }

    [RequiresAws]
    public void GetSignedUploadUrl_WithOptions_MismatchedHeaders_Rejected()
    {
        using var hub = CreateHub();
        var dir = (ISignedUploadable)hub.Root;
        var name = $"signed-upload-mismatch-{Guid.NewGuid():N}.txt";
        var options = new S3WriteOptions { ContentType = "image/png" };

        var url = dir.GetSignedUploadUrl(name, TimeSpan.FromMinutes(5), options);

        using var http = new System.Net.Http.HttpClient();
        using var content = new System.Net.Http.ByteArrayContent(Encoding.UTF8.GetBytes("x"));
        // Caller sends a different Content-Type than what was signed → S3 must reject.
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var resp = http.PutAsync(url, content).GetAwaiter().GetResult();

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(hub.Root.FileExists(name));
    }
}
