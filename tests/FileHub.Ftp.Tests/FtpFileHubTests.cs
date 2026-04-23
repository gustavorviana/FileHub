using FileHub.Ftp.Tests.Fakes;

namespace FileHub.Ftp.Tests;

public class FtpFileHubTests
{
    [Fact]
    public void FromFtpClient_BuildsRootDirectoryAtServerRoot()
    {
        using var fake = new InMemoryFtpClient();
        using var hub = FtpFileHubTestAccess.FromFtpClient(fake);

        Assert.NotNull(hub.Root);
        Assert.Equal("/", hub.Root.Path);
    }

    [Fact]
    public void FromFtpClient_WithRootPath_NormalizesAndCreatesTree()
    {
        using var fake = new InMemoryFtpClient();
        using var hub = FtpFileHubTestAccess.FromFtpClient(fake, "uploads/2026");

        Assert.Equal("/uploads/2026", hub.Root.Path);
        // The server should have walked both "uploads" and "2026" into existence.
        Assert.NotNull(fake.Server.Find("/uploads"));
        Assert.NotNull(fake.Server.Find("/uploads/2026"));
    }

    [Fact]
    public void FromFtpClient_TrailingSlashIsStripped()
    {
        using var fake = new InMemoryFtpClient();
        using var hub = FtpFileHubTestAccess.FromFtpClient(fake, "/data/");

        Assert.Equal("/data", hub.Root.Path);
    }

    [Fact]
    public void Dispose_OwnsClient_DisposesIt()
    {
        var fake = new InMemoryFtpClient();
        var hub = FtpFileHubTestAccess.FromFtpClient(fake);

        hub.Dispose();

        Assert.Throws<ObjectDisposedException>(() => fake.ConnectAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var fake = new InMemoryFtpClient();
        var hub = FtpFileHubTestAccess.FromFtpClient(fake);

        hub.Dispose();
        hub.Dispose(); // should not throw
    }

    [Fact]
    public void Connect_GuardsAgainstInvalidPort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FtpFileHub.Connect("localhost", port: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FtpFileHub.Connect("localhost", port: 70_000));
    }

    [Fact]
    public void Connect_GuardsAgainstEmptyHost()
    {
        Assert.Throws<ArgumentException>(() =>
            FtpFileHub.Connect(host: ""));
    }
}
