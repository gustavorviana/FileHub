using FileHub.Memory;

namespace FileHub.Tests;

public class FileHubExtensionsTests
{
    private static FileDirectory NewRoot() => new MemoryFileHub().Root;

    // === File.AsReadOnly ===

    [Fact]
    public void File_AsReadOnly_MarksIsReadOnly()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.True(ro.IsReadOnly);
        Assert.False(file.IsReadOnly);
    }

    [Fact]
    public void File_AsReadOnly_ExposesReadOperations()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("payload");

        var ro = file.AsReadOnly();

        Assert.Equal("payload", ro.ReadAllText());
        Assert.Equal(file.Length, ro.Length);
        Assert.Equal(file.Name, ro.Name);
        Assert.Equal(file.Path, ro.Path);
        Assert.True(ro.Exists());
    }

    [Fact]
    public void File_AsReadOnly_WriteStream_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.GetWriteStream());
    }

    [Fact]
    public void File_AsReadOnly_SetText_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.SetText("x"));
    }

    [Fact]
    public void File_AsReadOnly_SetBytes_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.SetBytes(new byte[] { 1 }));
    }

    [Fact]
    public void File_AsReadOnly_Delete_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.Delete());
    }

    [Fact]
    public void File_AsReadOnly_Rename_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.Rename("b.txt"));
    }

    [Fact]
    public void File_AsReadOnly_MoveTo_Throws()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        var dst = root.CreateDirectory("dst");
        var ro = file.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.MoveTo(dst, "b.txt"));
    }

    [Fact]
    public void File_AsReadOnly_Parent_IsAlsoReadOnly()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        var ro = file.AsReadOnly();

        Assert.True(ro.Parent.IsReadOnly);
    }

    [Fact]
    public void File_AsReadOnly_TwiceReturnsSameInstance()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ro1 = file.AsReadOnly();
        var ro2 = ro1.AsReadOnly();

        Assert.Same(ro1, ro2);
    }

    // === Directory.AsReadOnly ===

    [Fact]
    public void Directory_AsReadOnly_MarksIsReadOnly()
    {
        var root = NewRoot();
        var ro = root.AsReadOnly();

        Assert.True(ro.IsReadOnly);
        Assert.False(root.IsReadOnly);
    }

    [Fact]
    public void Directory_AsReadOnly_ExposesReadOperations()
    {
        var root = NewRoot();
        root.CreateFile("a.txt").SetText("hi");
        root.CreateDirectory("sub");

        var ro = root.AsReadOnly();

        Assert.True(ro.ItemExists("a.txt"));
        Assert.True(ro.Exists());
        Assert.True(ro.TryOpenFile("a.txt", out var f));
        Assert.True(f.IsReadOnly);
        Assert.Equal("hi", f.ReadAllText());
        Assert.True(ro.TryOpenDirectory("sub", out var d));
        Assert.True(d.IsReadOnly);
    }

    [Fact]
    public void Directory_AsReadOnly_GetFiles_AllReadOnly()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");

        var ro = root.AsReadOnly();

        Assert.All(ro.GetFiles(), f => Assert.True(f.IsReadOnly));
        Assert.Equal(2, ro.GetFiles().Count());
    }

    [Fact]
    public void Directory_AsReadOnly_GetDirectories_AllReadOnly()
    {
        var root = NewRoot();
        root.CreateDirectory("a");
        root.CreateDirectory("b");

        var ro = root.AsReadOnly();

        Assert.All(ro.GetDirectories(), d => Assert.True(d.IsReadOnly));
    }

    [Fact]
    public void Directory_AsReadOnly_CreateFile_Throws()
    {
        var ro = NewRoot().AsReadOnly();
        Assert.Throws<FileHubException>(() => ro.CreateFile("x"));
    }

    [Fact]
    public void Directory_AsReadOnly_CreateDirectory_Throws()
    {
        var ro = NewRoot().AsReadOnly();
        Assert.Throws<FileHubException>(() => ro.CreateDirectory("x"));
    }

    [Fact]
    public void Directory_AsReadOnly_Delete_Throws()
    {
        var ro = NewRoot().AsReadOnly();
        Assert.Throws<FileHubException>(() => ro.Delete());
        Assert.Throws<FileHubException>(() => ro.Delete("x"));
    }

    [Fact]
    public void Directory_AsReadOnly_Rename_Throws()
    {
        var ro = NewRoot().AsReadOnly();
        Assert.Throws<FileHubException>(() => ro.Rename("x"));
    }

    [Fact]
    public void Directory_AsReadOnly_MoveTo_Throws()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        var dst = root.CreateDirectory("dst");
        var ro = src.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.MoveTo(dst, "x"));
    }

    [Fact]
    public void Directory_AsReadOnly_CopyTo_Throws()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        var dst = root.CreateDirectory("dst");
        var ro = src.AsReadOnly();

        Assert.Throws<FileHubException>(() => ro.CopyTo(dst, "x"));
    }

    [Fact]
    public void Directory_AsReadOnly_TryOpenFile_Missing_ReturnsFalse()
    {
        var ro = NewRoot().AsReadOnly();
        Assert.False(ro.TryOpenFile("ghost.txt", out var f));
        Assert.Null(f);
    }

    [Fact]
    public void Directory_AsReadOnly_TryOpenDirectory_Missing_ReturnsFalse()
    {
        var ro = NewRoot().AsReadOnly();
        Assert.False(ro.TryOpenDirectory("ghost", out var d));
        Assert.Null(d);
    }

    [Fact]
    public void Directory_AsReadOnly_TwiceReturnsSameInstance()
    {
        var root = NewRoot();
        var ro1 = root.AsReadOnly();
        var ro2 = ro1.AsReadOnly();

        Assert.Same(ro1, ro2);
    }

    [Fact]
    public void Directory_AsReadOnly_SubDirectory_ParentIsAlsoReadOnly()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("sub");
        var ro = sub.AsReadOnly();

        Assert.True(ro.Parent == null || ro.Parent.IsReadOnly);
    }

    [Fact]
    public void Directory_AsReadOnly_Nested_OpenedFileIsReadOnly()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("sub");
        sub.CreateFile("a.txt").SetText("x");

        var ro = root.AsReadOnly();
        Assert.True(ro.TryOpenDirectory("sub", out var roSub));
        Assert.True(roSub.TryOpenFile("a.txt", out var f));

        Assert.True(f.IsReadOnly);
        Assert.Equal("x", f.ReadAllText());
        Assert.Throws<FileHubException>(() => f.SetText("y"));
    }
}
