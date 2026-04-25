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
    public void GetFiles_WithOffset_SkipsFirstN()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");
        root.CreateFile("c.txt");

        var names = root.GetFiles(offset: 1).Select(f => f.Name).OrderBy(n => n).ToArray();

        Assert.Equal(2, names.Length);
    }

    [Fact]
    public void GetFiles_WithLimit_CapsResults()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");
        root.CreateFile("c.txt");

        var names = root.GetFiles(limit: 2).ToArray();

        Assert.Equal(2, names.Length);
    }

    [Fact]
    public void GetFiles_OffsetAndLimit_PaginateSlice()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        for (int i = 0; i < 5; i++) root.CreateFile($"f{i}.txt");

        var first = root.GetFiles(offset: 0, limit: 2).Select(f => f.Name).ToArray();
        var second = root.GetFiles(offset: 2, limit: 2).Select(f => f.Name).ToArray();

        Assert.Equal(2, first.Length);
        Assert.Equal(2, second.Length);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void GetFiles_NegativeOffset_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.Throws<ArgumentOutOfRangeException>(() => root.GetFiles(offset: -1).ToArray());
    }

    [Fact]
    public void GetFiles_NegativeLimit_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.Throws<ArgumentOutOfRangeException>(() => root.GetFiles(limit: -1).ToArray());
    }

    [Fact]
    public async Task GetFilesAsync_WithOffsetAndLimit_PaginateSlice()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        for (int i = 0; i < 5; i++) root.CreateFile($"f{i}.txt");

        var names = new List<string>();
        await foreach (var f in root.GetFilesAsync(offset: 1, limit: 2))
            names.Add(f.Name);

        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void GetFiles_NamedOffset_StartsFromCursorInclusive()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");
        root.CreateFile("c.txt");
        root.CreateFile("d.txt");

        var names = root.GetFiles(offset: FileListOffset.FromName("c.txt"))
            .Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "c.txt", "d.txt" }, names);
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

        Assert.True(root.FileExists("a.txt"));
        Assert.True(root.DirectoryExists("sub"));
        Assert.False(root.DirectoryExists("ghost"));
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

    // === Path traversal protection ===

    [Fact]
    public void CreateFile_WithParentTraversal_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        // Parent-directory traversal is rejected by SplitPath before any filesystem call.
        Assert.Throws<FileHubException>(() => root.CreateFile("..\\escape.txt"));
    }

    [Fact]
    public void OpenDirectory_NestedPath_NavigatesIntoSubdirectory()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateDirectory("folder1").CreateDirectory("folder2");

        var opened = root.OpenDirectory("folder1/folder2");

        Assert.Equal("folder2", opened.Name);
        Assert.True(opened.Exists());
    }

    [Fact]
    public void OpenDirectory_NestedPath_BackslashSeparator_Works()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateDirectory("folder1").CreateDirectory("folder2");

        var opened = root.OpenDirectory("folder1\\folder2");

        Assert.Equal("folder2", opened.Name);
    }

    [Fact]
    public void OpenDirectory_NestedPath_CreateIfNotExists_CreatesChain()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var opened = root.OpenDirectory("folder1/folder2/folder3", createIfNotExists: true);

        Assert.True(opened.Exists());
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "folder1", "folder2", "folder3")));
    }

    [Fact]
    public void OpenFile_NestedPath_OpensFileInSubdirectory()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateDirectory("folder1").CreateFile("file.txt").SetText("payload");

        var opened = root.OpenFile("folder1/file.txt");

        Assert.Equal("file.txt", opened.Name);
        Assert.Equal("payload", opened.ReadAllText());
    }

    [Fact]
    public void OpenDirectory_ParentTraversal_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("sub");

        Assert.Throws<FileHubException>(() => sub.OpenDirectory("../escape"));
    }

    [Fact]
    public void OpenFile_ParentTraversal_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var sub = root.CreateDirectory("sub");

        Assert.Throws<FileHubException>(() => sub.OpenFile("../escape.txt"));
    }

    [Fact]
    public void OpenDirectory_LeadingSlash_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        root.CreateDirectory("folder1");

        Assert.Throws<FileHubException>(() => root.OpenDirectory("/folder1"));
        Assert.Throws<FileHubException>(() => root.OpenDirectory("\\folder1"));
    }

    [Fact]
    public void OpenFile_LeadingSlash_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.Throws<FileHubException>(() => root.OpenFile("/file.txt"));
        Assert.Throws<FileHubException>(() => root.OpenFile("\\file.txt"));
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

    // === Nested-path directory creation / lookup ===

    [Fact]
    public void CreateDirectory_ForwardSlash_CreatesIntermediate()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        var leaf = root.CreateDirectory("a/b/c");

        Assert.Equal("c", leaf.Name);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "a", "b", "c")));
        Assert.True(root.TryOpenDirectory("a/b/c", out _));
    }

    [Fact]
    public void CreateDirectory_Backslash_CreatesIntermediate()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        root.CreateDirectory("x\\y");

        Assert.True(Directory.Exists(Path.Combine(temp.Path, "x", "y")));
    }

    [Fact]
    public void CreateDirectory_Nested_ReusesExistingIntermediate()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var firstA = root.CreateDirectory("a");
        firstA.CreateFile("keep.txt");

        root.CreateDirectory("a/b");

        Assert.True(File.Exists(Path.Combine(temp.Path, "a", "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "a", "b")));
    }

    [Fact]
    public void TryOpenDirectory_NestedPath_ReturnsFalseWhenIntermediateMissing()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.False(root.TryOpenDirectory("missing/child", out var dir));
        Assert.Null(dir);
    }

    [Fact]
    public void CreateDirectory_AbsolutePath_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.Throws<FileHubException>(() => root.CreateDirectory("/abs"));
        Assert.Throws<FileHubException>(() => root.CreateDirectory("\\abs"));
    }

    [Fact]
    public void CreateDirectory_ParentTraversal_Throws()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);

        Assert.Throws<FileHubException>(() => root.CreateDirectory("../escape"));
        Assert.Throws<FileHubException>(() => root.CreateDirectory("a/../escape"));
    }

    // === DirectoryPathMode: Direct vs OpenIntermediates ===

    [Fact]
    public void CreateDirectory_DirectMode_CreatesFullTreeInSingleCall()
    {
        using var temp = new TempDirectory();
        var root = new LocalFileHub(temp.Path, DirectoryPathMode.Direct).Root;

        var leaf = root.CreateDirectory("a/b/c");

        Assert.Equal("c", leaf.Name);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "a", "b", "c")));
        Assert.True(root.TryOpenDirectory("a/b/c", out _));
    }

    [Fact]
    public void CreateDirectory_DirectMode_ParentTraversal_Throws()
    {
        using var temp = new TempDirectory();
        var root = new LocalFileHub(temp.Path, DirectoryPathMode.Direct).Root;

        Assert.Throws<FileHubException>(() => root.CreateDirectory("../escape"));
        Assert.Throws<FileHubException>(() => root.CreateDirectory("a/../escape"));
    }

    [Fact]
    public void TryOpenDirectory_DirectMode_Missing_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var root = new LocalFileHub(temp.Path, DirectoryPathMode.Direct).Root;

        Assert.False(root.TryOpenDirectory("x/y/z", out var dir));
        Assert.Null(dir);
    }
}
