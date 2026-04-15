using FileHub.Local;

namespace FileHub.Tests;

public class LocalDirectoryTests
{
    private static FileDirectory NewRoot(TempDirectory temp) =>
        new LocalFileHub(temp.Path).Root;

    // === CreateFile / CreateDirectory ===

    [Fact]
    public void CreateFile_CreatesFileOnDisk()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var file = root.CreateFile("a.txt");

        Assert.True(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.True(file.Exists());
    }

    [Fact]
    public void CreateFile_InvalidName_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        Assert.Throws<ArgumentException>(() => root.CreateFile(""));
    }

    [Fact]
    public void CreateFile_Overwrite_ReplacesExisting()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt").SetText("old");

        var file = root.CreateFile("a.txt", overwrite: true);

        Assert.Equal(0, file.Length);
    }

    [Fact]
    public void CreateDirectory_CreatesDirectoryOnDisk()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var sub = root.CreateDirectory("child");

        Assert.True(Directory.Exists(Path.Combine(temp.Path, "child")));
        Assert.Equal("child", sub.Name);
    }

    // === TryOpen / Open ===

    [Fact]
    public void TryOpenFile_Existing_ReturnsTrue()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");

        Assert.True(root.TryOpenFile("a.txt", out var file));
        Assert.NotNull(file);
    }

    [Fact]
    public void TryOpenFile_Missing_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.False(root.TryOpenFile("missing.txt", out _));
    }

    [Fact]
    public void OpenFile_Missing_ThrowsFileNotFound()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        Assert.Throws<FileNotFoundException>(() => root.OpenFile("missing.txt"));
    }

    [Fact]
    public void OpenFile_CreateIfNotExists_Creates()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var file = root.OpenFile("new.txt", createIfNotExists: true);

        Assert.True(file.Exists());
    }

    [Fact]
    public void TryOpenDirectory_Existing_ReturnsTrue()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateDirectory("sub");

        Assert.True(root.TryOpenDirectory("sub", out var dir));
        Assert.NotNull(dir);
    }

    [Fact]
    public void TryOpenDirectory_Missing_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        Assert.False(root.TryOpenDirectory("missing", out _));
    }

    // === GetFiles / GetDirectories ===

    [Fact]
    public void GetFiles_ReturnsAllFiles()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateFile("b.log");

        var names = root.GetFiles().Select(f => f.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "a.txt", "b.log" }, names);
    }

    [Fact]
    public void GetFiles_WithPattern_FiltersByExtension()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateFile("b.log");
        root.CreateFile("c.txt");

        var names = root.GetFiles("*.txt").Select(f => f.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "a.txt", "c.txt" }, names);
    }

    [Fact]
    public void GetDirectories_ReturnsSubdirectories()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateDirectory("one");
        root.CreateDirectory("two");

        var names = root.GetDirectories().Select(d => d.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "one", "two" }, names);
    }

    // === ItemExists / Exists ===

    [Fact]
    public void ItemExists_ReturnsCorrectResult()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateDirectory("sub");

        Assert.True(root.ItemExists("a.txt"));
        Assert.True(root.ItemExists("sub"));
        Assert.False(root.ItemExists("ghost"));
    }

    // === Delete ===

    [Fact]
    public void Delete_ByName_RemovesFile()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");

        root.Delete("a.txt");

        Assert.False(File.Exists(Path.Combine(temp.Path, "a.txt")));
    }

    [Fact]
    public void Delete_ByName_RemovesDirectoryRecursively()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("sub");
        sub.CreateFile("nested.txt");

        root.Delete("sub");

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "sub")));
    }

    [Fact]
    public void Delete_Missing_ThrowsFileNotFound()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        Assert.Throws<FileNotFoundException>(() => root.Delete("ghost"));
    }

    [Fact]
    public void Delete_WholeDirectory_Works()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("sub");
        sub.CreateFile("a.txt");

        sub.Delete();

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "sub")));
    }

    [Fact]
    public void DeleteIfExists_Missing_NoOp()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var ex = Record.Exception(() => root.DeleteIfExists("ghost"));
        Assert.Null(ex);
    }

    // === Rename ===

    [Fact]
    public void Rename_RenamesDirectoryOnDisk()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("old");

        var renamed = sub.Rename("new");

        Assert.Equal("new", renamed.Name);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "new")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "old")));
    }

    // === Move/Copy ===

    [Fact]
    public void MoveTo_MovesDirectoryContents()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateDirectory("src");
        src.CreateFile("f.txt").SetText("payload");
        var dst = root.CreateDirectory("dst");

        var moved = src.MoveTo(dst, "moved");

        Assert.True(Directory.Exists(Path.Combine(temp.Path, "dst", "moved")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "src")));
        Assert.True(moved.TryOpenFile("f.txt", out var f));
        Assert.Equal("payload", f.ReadAllText());
    }

    [Fact]
    public void CopyTo_CopiesDirectoryContentsRecursively()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateDirectory("src");
        src.CreateFile("a.txt").SetText("hello");
        src.CreateDirectory("inner").CreateFile("b.txt").SetText("world");

        var copy = src.CopyTo(root, "copy");

        Assert.True(copy.TryOpenFile("a.txt", out var a));
        Assert.Equal("hello", a.ReadAllText());
        Assert.True(copy.TryOpenDirectory("inner", out var inner));
        Assert.True(inner.TryOpenFile("b.txt", out var b));
        Assert.Equal("world", b.ReadAllText());
        Assert.True(src.Exists());
    }

    // === SetLastWriteTime ===

    [Fact]
    public void SetLastWriteTime_UpdatesTimestamp()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("sub");
        var ts = new DateTime(2020, 5, 5, 0, 0, 0, DateTimeKind.Utc);

        sub.SetLastWriteTime(ts);

        Assert.Equal(ts, sub.LastWriteTimeUtc);
    }

    // === Path traversal protection ===

    [Fact]
    public void CreateFile_WithPathSeparator_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        // path-separator character is an invalid filename character on Windows and
        // should be rejected by ValidateName long before any filesystem call.
        Assert.Throws<ArgumentException>(() => root.CreateFile("..\\escape.txt"));
    }

    [Fact]
    public void Rename_ValidName_Works()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("a");

        var renamed = sub.Rename("b");

        Assert.Equal("b", renamed.Name);
    }

    // === Async wrappers ===

    [Fact]
    public async Task CreateFileAsync_Works()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var file = await root.CreateFileAsync("a.txt");

        Assert.True(file.Exists());
    }

    [Fact]
    public async Task GetFilesAsync_EnumeratesFiles()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");

        var count = 0;
        await foreach (var _ in root.GetFilesAsync())
            count++;

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrue()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.True(await root.ExistsAsync());
    }
}
