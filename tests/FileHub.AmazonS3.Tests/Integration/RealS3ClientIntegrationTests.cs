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
        return AmazonS3FileHub.FromCredentials(rootPath: "filehub-integration", bucketName: bucket, credentials: credentials, region: region);
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
}
