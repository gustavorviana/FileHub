using FileHub.Local;
using System.Text;

namespace FileHub.Tests;

public class LocalFileTests
{
    private static FileDirectory NewRoot(TempDirectory temp) =>
        new LocalFileHub(temp.Path).Root;

    [Fact]
    public void SetText_ReadAllText_Roundtrip()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.txt");

        file.SetText("hello");

        Assert.Equal("hello", file.ReadAllText());
    }

    [Fact]
    public void SetBytes_ReadAllBytes_Roundtrip()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.bin");
        var payload = new byte[] { 1, 2, 3 };

        file.SetBytes(payload);

        Assert.Equal(payload, file.ReadAllBytes());
    }

    [Fact]
    public void Length_ReflectsFileSize()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.bin");

        file.SetBytes(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(4, file.Length);
    }

    [Fact]
    public void Extension_ReturnsExtension()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        Assert.Equal(".txt", root.CreateFile("a.txt").Extension);
        Assert.Equal("", root.CreateFile("NoExt").Extension);
    }

    [Fact]
    public void Path_IncludesParentPath()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.txt");
        Assert.Equal(Path.Combine(temp.Path, "a.txt"), file.Path);
    }

    [Fact]
    public void CopyTo_SameDirectory_CreatesCopy()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");
        file.SetText("data");

        var copy = file.CopyTo("b.txt");

        Assert.Equal("data", copy.ReadAllText());
        Assert.True(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "b.txt")));
    }

    [Fact]
    public void CopyTo_OtherDirectory_Works()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");
        file.SetText("data");
        var sub = root.CreateDirectory("sub");

        var copy = file.CopyTo(sub, "a_copy.txt");

        Assert.Equal("data", copy.ReadAllText());
        Assert.True(File.Exists(Path.Combine(temp.Path, "sub", "a_copy.txt")));
    }

    [Fact]
    public void Rename_ChangesFileNameOnDisk()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");
        file.SetText("keep");

        file.Rename("b.txt");

        Assert.False(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "b.txt")));
        Assert.Equal("keep", file.ReadAllText());
    }

    [Fact]
    public void Rename_InvalidName_Throws()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.txt");
        Assert.Throws<ArgumentException>(() => file.Rename(""));
    }

    [Fact]
    public void MoveTo_MovesFile()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");
        file.SetText("x");
        var dst = root.CreateDirectory("dst");

        var moved = file.MoveTo(dst, "moved.txt");

        Assert.False(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "dst", "moved.txt")));
        Assert.Equal("x", moved.ReadAllText());
    }

    [Fact]
    public void Delete_RemovesFileFromDisk()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");

        file.Delete();

        Assert.False(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.False(file.Exists());
    }

    [Fact]
    public void CreationTimeUtc_IsPopulated()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.txt");
        Assert.True(file.CreationTimeUtc > new DateTime(2000, 1, 1));
    }

    [Fact]
    public async Task ReadAllTextAsync_Works()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.txt");
        file.SetText("async");

        Assert.Equal("async", await file.ReadAllTextAsync());
    }

    [Fact]
    public async Task SetTextAsync_Works()
    {
        using var temp = new TempDirectory();
        var file = NewRoot(temp).CreateFile("a.txt");

        await file.SetTextAsync("written", Encoding.UTF8);

        Assert.Equal("written", file.ReadAllText());
    }

    [Fact]
    public async Task DeleteAsync_Works()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");

        await file.DeleteAsync();

        Assert.False(file.Exists());
    }
}
