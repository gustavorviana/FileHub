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

        using var hub = await AmazonS3FileHub.CreateAsync(
            S3HubOptions.FromClient("bucket", sdkClient));

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_ClientWithSdkConfig_Throws()
    {
        using var sdkClient = new Amazon.S3.AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials("ak", "sk"), Amazon.RegionEndpoint.USWest2);
        var options = new S3HubOptions
        {
            BucketName = "bucket",
            Client = sdkClient,
            SdkConfig = new Amazon.S3.AmazonS3Config(),
        };

        var ex = await Assert.ThrowsAsync<System.ArgumentException>(
            () => AmazonS3FileHub.CreateAsync(options));
        Assert.Contains("SdkConfig", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_Credentials_RegionComesFromSdkConfig()
    {
        var options = new S3HubOptions
        {
            BucketName = "bucket",
            Credentials = new Amazon.Runtime.BasicAWSCredentials("ak", "sk"),
            SdkConfig = new Amazon.S3.AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.USWest2 },
        };

        using var hub = await AmazonS3FileHub.CreateAsync(options);

        Assert.NotNull(hub.Root);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_ExplicitRegionWinsOverSdkConfig()
    {
        // RegionEndpoint's getter falls back to the host environment when
        // unset, so the hub always assigns the resolved Region on top of the
        // supplied config instead of trying to detect "already set".
        var config = new Amazon.S3.AmazonS3Config { RegionEndpoint = Amazon.RegionEndpoint.USWest2 };
        var options = new S3HubOptions
        {
            BucketName = "bucket",
            Credentials = new Amazon.Runtime.BasicAWSCredentials("ak", "sk"),
            Region = "us-east-1",
            SdkConfig = config,
        };

        using var hub = await AmazonS3FileHub.CreateAsync(options);

        Assert.Equal(Amazon.RegionEndpoint.USEast1, config.RegionEndpoint);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromClient_AmazonS3Client_Sync_Works()
    {
        var credentials = new Amazon.Runtime.BasicAWSCredentials("ak", "sk");
        using var sdkClient = new Amazon.S3.AmazonS3Client(credentials, Amazon.RegionEndpoint.EUWest1);

        using var hub = AmazonS3FileHub.Create(
            S3HubOptions.FromClient("bucket", sdkClient));

        Assert.NotNull(hub.Root);
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
