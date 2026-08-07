namespace FileHub.Ftp.Tests;

/// <summary>
/// Move/copy-onto-itself and into-own-descendant business rules for the FTP
/// driver, exercised against the in-memory FTP server (no Docker). Onto-itself
/// is a <see cref="FileAlreadyExistsException"/>; into a descendant is a
/// <see cref="FileHubException"/>.
/// </summary>
public class FtpSelfMoveCopyTests : FtpTestBase
{
    [Fact]
    public void File_MoveTo_OntoItself_Throws_AndKeepsFile()
    {
        var file = Root.CreateFile("a.txt");
        file.SetText("keep");

        Assert.Throws<FileAlreadyExistsException>(() => file.MoveTo(Root, "a.txt"));

        Assert.True(Root.FileExists("a.txt"));
    }

    [Fact]
    public void File_CopyTo_OntoItself_Throws_AndKeepsFile()
    {
        var file = Root.CreateFile("a.txt");
        file.SetText("keep");

        Assert.Throws<FileAlreadyExistsException>(() => file.CopyTo(Root, "a.txt"));

        Assert.True(Root.FileExists("a.txt"));
    }

    [Fact]
    public void File_CopyTo_DifferentName_IsNotBlocked()
    {
        var file = Root.CreateFile("a.txt");
        file.SetText("payload");

        file.CopyTo(Root, "b.txt");

        Assert.True(Root.FileExists("a.txt"));
        Assert.True(Root.FileExists("b.txt"));
    }

    [Fact]
    public void Directory_MoveTo_OntoItself_Throws()
    {
        var dir = Root.CreateDirectory("d1");
        dir.CreateFile("f.txt").SetText("data");

        Assert.Throws<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").MoveTo(Root, "d1"));

        Assert.True(Root.DirectoryExists("d1"));
    }

    [Fact]
    public void Directory_CopyTo_OntoItself_Throws()
    {
        var dir = Root.CreateDirectory("d1");
        dir.CreateFile("f.txt").SetText("data");

        Assert.Throws<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").CopyTo(Root, "d1"));
    }

    [Fact]
    public void Directory_MoveTo_IntoOwnDescendant_Throws()
    {
        var outer = Root.CreateDirectory("outer");
        var inner = outer.CreateDirectory("inner");

        Assert.Throws<FileHubException>(() => outer.MoveTo(inner, "moved"));
    }

    [Fact]
    public void Directory_CopyTo_IntoOwnDescendant_Throws()
    {
        var outer = Root.CreateDirectory("outer");
        var inner = outer.CreateDirectory("inner");

        Assert.Throws<FileHubException>(() => outer.CopyTo(inner, "copied"));
    }
}
