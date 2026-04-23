using System.Text;
using FileHub.Ftp.Tests.Fakes;

namespace FileHub.Ftp.Tests;

public class FtpFileTests : FtpTestBase
{
    [Fact]
    public void SetText_ThenReadAllText_Roundtrips()
    {
        var file = Root.CreateFile("note.txt");
        file.SetText("alpha-beta", Encoding.UTF8);

        Assert.Equal("alpha-beta", file.ReadAllText());
        Assert.Equal(Encoding.UTF8.GetByteCount("alpha-beta"), file.Length);
    }

    [Fact]
    public void SetBytes_ThenReadAllBytes_Roundtrips()
    {
        var file = Root.CreateFile("blob.bin");
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        file.SetBytes(payload);

        Assert.Equal(payload, file.ReadAllBytes());
    }

    [Fact]
    public void Exists_ReflectsServerState()
    {
        var file = Root.CreateFile("x.txt");

        Assert.True(file.Exists());

        file.Delete();

        Assert.False(file.Exists());
    }

    [Fact]
    public void Rename_ChangesNameOnServer()
    {
        var file = Root.CreateFile("old.txt");
        file.SetText("data");

        file.Rename("new.txt");

        Assert.Equal("new.txt", file.Name);
        Assert.Equal("/new.txt", file.Path);
        Assert.Null(Client.Server.Find("/old.txt"));
        Assert.NotNull(Client.Server.Find("/new.txt"));
    }

    [Fact]
    public void MoveTo_SameConnection_UsesServerSideRename()
    {
        var dest = Root.CreateDirectory("dest");
        var file = Root.CreateFile("a.txt");
        file.SetText("payload");

        var moved = file.MoveTo(dest, "renamed.txt");

        Assert.Equal("/dest/renamed.txt", moved.Path);
        Assert.Null(Client.Server.Find("/a.txt"));
        Assert.NotNull(Client.Server.Find("/dest/renamed.txt"));
    }

    [Fact]
    public void MoveTo_DifferentConnection_UsesStreamCopyAndDelete()
    {
        using var otherClient = new InMemoryFtpClient();
        using var otherHub = FtpFileHubTestAccess.FromFtpClient(otherClient);

        var file = Root.CreateFile("data.txt");
        file.SetText("payload");

        var moved = file.MoveTo(otherHub.Root, "data.txt");

        Assert.Null(Client.Server.Find("/data.txt"));
        Assert.NotNull(otherClient.Server.Find("/data.txt"));
        Assert.Equal("payload", moved.ReadAllText());
    }

    [Fact]
    public void CopyTo_DuplicatesFile_LeavesSourceIntact()
    {
        var dest = Root.CreateDirectory("dest");
        var file = Root.CreateFile("a.txt");
        file.SetText("hi");

        var copy = file.CopyTo(dest, "a.txt");

        Assert.NotNull(Client.Server.Find("/a.txt"));
        Assert.NotNull(Client.Server.Find("/dest/a.txt"));
        Assert.Equal("hi", copy.ReadAllText());
    }

    [Fact]
    public void GetWriteStream_OverwritesExistingContent()
    {
        var file = Root.CreateFile("c.txt");
        file.SetText("first");

        using (var writer = file.GetWriteStream())
        {
            var bytes = Encoding.UTF8.GetBytes("second-longer");
            writer.Write(bytes, 0, bytes.Length);
        }

        Assert.Equal("second-longer", file.ReadAllText());
    }

    [Fact]
    public void GetReadStream_OpeningTwice_Throws()
    {
        var file = Root.CreateFile("d.txt");
        file.SetText("hi");

        var first = file.GetReadStream();
        try
        {
            Assert.Throws<InvalidOperationException>(() => file.GetReadStream());
        }
        finally
        {
            first.Dispose();
        }

        using var second = file.GetReadStream();
        Assert.NotNull(second);
    }

    [Fact]
    public void Session_ConnectsLazilyAndOnlyOnce()
    {
        var countBefore = Client.ConnectInvocationCount;

        Root.CreateFile("a.txt").SetText("hi");
        Root.CreateFile("b.txt").SetText("hi");
        Root.GetFiles().ToList();

        Assert.True(Client.ConnectInvocationCount >= 1);
        Assert.Equal(countBefore == 0 ? 1 : countBefore, Client.ConnectInvocationCount);
    }
}
