using FileHub.Ftp.Tests.Fakes;

namespace FileHub.Ftp.Tests;

/// <summary>
/// When a move copies successfully but the source delete fails, the driver must
/// surface a <see cref="PartialMoveException"/>. FTP uses a native rename within
/// the same connection (no separate delete), so the copy+delete path is exercised
/// across two connections, with the source client's delete-fault injector.
/// </summary>
public class FtpPartialMoveTests
{
    [Fact]
    public async Task File_MoveTo_CrossConnection_DeleteFails_ThrowsPartialMove_CopyKept()
    {
        using var srcClient = new InMemoryFtpClient();
        using var dstClient = new InMemoryFtpClient();
        using var hubA = FtpFileHub.FromFtpClient(srcClient);
        using var hubB = FtpFileHub.FromFtpClient(dstClient);
        var file = hubA.Root.CreateFile("a.txt");
        file.SetText("payload");
        srcClient.DeleteFailureInjector = _ => new IOException("delete boom");

        await Assert.ThrowsAsync<PartialMoveException>(
            () => file.MoveToAsync(hubB.Root, "a.txt"));

        Assert.True(hubB.Root.FileExists("a.txt"));   // copy landed on the destination
        Assert.True(hubA.Root.FileExists("a.txt"));   // source kept (delete failed)
    }
}
