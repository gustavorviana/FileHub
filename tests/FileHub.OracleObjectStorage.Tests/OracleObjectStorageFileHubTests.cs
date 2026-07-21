using System;
using FileHub.OracleObjectStorage.Internal;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageFileHubTests
{
    [Fact]
    public void FromOciClient_BuildsRootDirectory()
    {
        using var fake = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(fake);

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
        Assert.True(hub.Root.Exists() || hub.Root.GetFiles().Any() == false);
    }

    [Fact]
    public void FromOciClient_WithRootPath_NormalizesAndCreatesMarker()
    {
        using var fake = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(fake, "uploads/2026");

        Assert.Equal("/uploads/2026", hub.Root.Path);
        // Marker object was created under the normalized prefix.
        Assert.True(fake.TryGetBody("uploads/2026/", out _));
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateAsync_ClientWithRetryConfiguration_Throws()
    {
        var provider = new Oci.Common.Auth.SimpleAuthenticationDetailsProvider
        {
            TenantId = "ocid1.tenancy.oc1..fake",
            UserId = "ocid1.user.oc1..fake",
            Fingerprint = "aa:bb",
            Region = Oci.Common.Region.SA_SAOPAULO_1,
        };
        using var sdkClient = new Oci.ObjectstorageService.ObjectStorageClient(provider);
        var options = new OciHubOptions
        {
            BucketName = "bucket",
            Client = sdkClient,
            RegionId = "sa-saopaulo-1",
            Namespace = "ns",
            RetryConfiguration = new Oci.Common.Retry.RetryConfiguration(),
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => OracleObjectStorageFileHub.CreateAsync(options));
        Assert.Contains("RetryConfiguration", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromClient_ObjectStorageClient_Async_Works()
    {
        var provider = new Oci.Common.Auth.SimpleAuthenticationDetailsProvider
        {
            TenantId = "ocid1.tenancy.oc1..fake",
            UserId = "ocid1.user.oc1..fake",
            Fingerprint = "aa:bb",
            Region = Oci.Common.Region.SA_SAOPAULO_1,
        };
        using var sdkClient = new Oci.ObjectstorageService.ObjectStorageClient(provider);

        using var hub = await OracleObjectStorageFileHub.CreateAsync(
            OciHubOptions.FromClient("bucket", sdkClient, "sa-saopaulo-1", "ns"));

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
    }

    [Fact]
    public void FromClient_ObjectStorageClient_Sync_Works()
    {
        var provider = new Oci.Common.Auth.SimpleAuthenticationDetailsProvider
        {
            TenantId = "ocid1.tenancy.oc1..fake",
            UserId = "ocid1.user.oc1..fake",
            Fingerprint = "aa:bb",
            Region = Oci.Common.Region.SA_SAOPAULO_1,
        };
        using var sdkClient = new Oci.ObjectstorageService.ObjectStorageClient(provider);

        using var hub = OracleObjectStorageFileHub.Create(
            OciHubOptions.FromClient("bucket", sdkClient, "sa-saopaulo-1", "ns"));

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
    }

    [Fact]
    public void Dispose_OwnsClient_DisposesIt()
    {
        var fake = new InMemoryOciClient();
        var hub = OracleObjectStorageFileHub.FromOciClient(fake);

        hub.Dispose();

        // After dispose, the underlying fake also disposed — further calls should throw.
        Assert.Throws<ObjectDisposedException>(() => fake.HeadObjectAsync("anything").GetAwaiter().GetResult());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var fake = new InMemoryOciClient();
        var hub = OracleObjectStorageFileHub.FromOciClient(fake);

        hub.Dispose();
        hub.Dispose(); // should not throw
    }
}
