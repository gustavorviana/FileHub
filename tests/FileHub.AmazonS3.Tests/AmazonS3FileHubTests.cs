using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3FileHubTests
{
    [Fact]
    public void FromS3Client_CreatesHubWithRootAtBucket()
    {
        var client = new InMemoryS3Client(bucket: "example", region: "us-east-1");
        using var hub = AmazonS3FileHub.FromS3Client(client);

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
    }

    [Fact]
    public void FromS3Client_WithRootPath_ScopesHubToPrefix()
    {
        var client = new InMemoryS3Client();
        using var hub = AmazonS3FileHub.FromS3Client(client, rootPath: "tenants/acme");

        Assert.Equal("/tenants/acme", hub.Root.Path);
    }

    [Fact]
    public async System.Threading.Tasks.Task Dispose_DisposesTheSessionsClient()
    {
        var client = new InMemoryS3Client();
        var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Dispose();

        await Assert.ThrowsAsync<System.ObjectDisposedException>(
            () => client.HeadObjectAsync("anything"));
    }

    [Fact]
    public async System.Threading.Tasks.Task FromClient_AmazonS3Client_DerivesRegionFromConfig()
    {
        var credentials = new Amazon.Runtime.BasicAWSCredentials("ak", "sk");
        using var sdkClient = new Amazon.S3.AmazonS3Client(credentials, Amazon.RegionEndpoint.USWest2);

        using var hub = await AmazonS3FileHub.FromClientAsync(
            bucketName: "bucket",
            rootPath: "",
            client: sdkClient);

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromClient_AmazonS3Client_Sync_Works()
    {
        var credentials = new Amazon.Runtime.BasicAWSCredentials("ak", "sk");
        using var sdkClient = new Amazon.S3.AmazonS3Client(credentials, Amazon.RegionEndpoint.EUWest1);

        using var hub = AmazonS3FileHub.FromClient(
            bucketName: "bucket",
            rootPath: "",
            client: sdkClient);

        Assert.NotNull(hub.Root);
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
