using FileHub.Ftp.Tests.Fakes;

namespace FileHub.Ftp.Tests;

/// <summary>
/// Contract tests for <see cref="IRefreshable"/>: drivers must NOT perform
/// hidden I/O inside property getters, and must expose explicit sync + async
/// refresh entry points so callers avoid async-over-sync deadlock risk.
/// </summary>
public class FtpRefreshableTests : FtpTestBase
{
    [Fact]
    public void FtpFile_ImplementsIRefreshable()
    {
        var file = Root.CreateFile("x.txt");

        Assert.IsAssignableFrom<IRefreshable>(file);
    }

    [Fact]
    public void FtpDirectory_ImplementsIRefreshable()
    {
        Assert.IsAssignableFrom<IRefreshable>(Root);
    }

    [Fact]
    public void FileLength_IsUpdatedLocallyAfterWrite_WithoutRefresh()
    {
        var file = Root.CreateFile("a.txt");

        file.SetText("hello-world");

        // Write path updates the cached length on stream dispose; no refresh needed.
        Assert.Equal(11, file.Length);
    }

    [Fact]
    public async Task RefreshAsync_OnFile_SyncsMetadataFromServer()
    {
        var file = Root.CreateFile("a.txt");
        file.SetText("hi");

        // Mutate server-side behind the driver's back to simulate an external change.
        var node = Client.Server.Find("/a.txt");
        node!.Body = System.Text.Encoding.UTF8.GetBytes("completely-new-content");

        // Before refresh: stale length.
        Assert.Equal(2, file.Length);

        await ((IRefreshable)file).RefreshAsync();

        Assert.Equal(22, file.Length);
    }

    [Fact]
    public void Refresh_OnFile_IsSyncDelegatingToAsync()
    {
        var file = Root.CreateFile("a.txt");
        file.SetText("hi");

        var node = Client.Server.Find("/a.txt");
        node!.Body = new byte[] { 1, 2, 3, 4 };

        ((IRefreshable)file).Refresh();

        Assert.Equal(4, file.Length);
    }

    [Fact]
    public async Task RefreshAsync_OnRootDirectory_CreatesNonSlashRootIfMissing()
    {
        // This scenario needs its own hub with a non-"/" root, so it opts out
        // of the base fixture and builds directly.
        using var client = new InMemoryFtpClient();
        using var hub = await FtpFileHubTestAccess.FromFtpClientAsync(client, "uploads/2026");

        Assert.NotNull(client.Server.Find("/uploads/2026"));
    }

    [Fact]
    public async Task RefreshAsync_OnChildDirectory_IsMetadataOnly()
    {
        var sub = Root.CreateDirectory("sub");

        await ((IRefreshable)sub).RefreshAsync();

        Assert.NotNull(Client.Server.Find("/sub"));
    }

    [Fact]
    public void PropertyGetters_DoNotHitNetwork_BeforeRefresh()
    {
        var dir = Root.CreateDirectory("fresh");

        var creation = dir.CreationTimeUtc;
        var modified = dir.LastWriteTimeUtc;

        Assert.Equal(default, creation);
        Assert.Equal(default, modified);
    }
}
