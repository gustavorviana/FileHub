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
