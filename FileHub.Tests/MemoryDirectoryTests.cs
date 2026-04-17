using FileHub.Memory;

namespace FileHub.Tests;

public class MemoryDirectoryTests
{
    private static FileDirectory NewRoot() => new MemoryFileHub().Root;

    // === CreateFile ===

    [Fact]
    public void CreateFile_CreatesFileWithGivenName()
    {
        var root = NewRoot();
        var file = root.CreateFile("hello.txt");

        Assert.Equal("hello.txt", file.Name);
        Assert.True(root.ItemExists("hello.txt"));
        Assert.True(file.Exists());
    }

    [Fact]
    public void CreateFile_InvalidName_Throws()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentException>(() => root.CreateFile(""));
        Assert.Throws<ArgumentException>(() => root.CreateFile(null));
        Assert.Throws<ArgumentException>(() => root.CreateFile("bad/name.txt"));
    }

    [Fact]
    public void CreateFile_Overwrite_ReplacesExisting()
    {
        var root = NewRoot();
        var first = root.CreateFile("x.txt");
        first.SetText("old");

        var second = root.CreateFile("x.txt", overwrite: true);

        Assert.True(root.ItemExists("x.txt"));
        Assert.Equal(0, second.Length);
    }

    [Fact]
    public void CreateFile_OverwriteFalse_KeepsFirstDeletedNothing()
    {
        var root = NewRoot();
        root.CreateFile("x.txt").SetText("hello");

        var again = root.CreateFile("x.txt", overwrite: false);

        Assert.Equal("x.txt", again.Name);
    }

    // === TryOpenFile / OpenFile ===

    [Fact]
    public void TryOpenFile_Existing_ReturnsTrue()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");

        var found = root.TryOpenFile("a.txt", out var file);

        Assert.True(found);
        Assert.NotNull(file);
        Assert.Equal("a.txt", file.Name);
    }

    [Fact]
    public void TryOpenFile_NotExisting_ReturnsFalseAndNull()
    {
        var root = NewRoot();

        var found = root.TryOpenFile("missing.txt", out var file);

        Assert.False(found);
        Assert.Null(file);
    }

    [Fact]
    public void OpenFile_Missing_ThrowsFileNotFound()
    {
        var root = NewRoot();
        Assert.Throws<FileNotFoundException>(() => root.OpenFile("x.txt"));
    }

    [Fact]
    public void OpenFile_CreateIfNotExists_CreatesWhenMissing()
    {
        var root = NewRoot();
        var file = root.OpenFile("new.txt", createIfNotExists: true);

        Assert.NotNull(file);
        Assert.True(root.ItemExists("new.txt"));
    }

    [Fact]
    public void OpenFile_CreateIfNotExists_ReturnsExisting()
    {
        var root = NewRoot();
        var created = root.CreateFile("keep.txt");
        created.SetText("stay");

        var opened = root.OpenFile("keep.txt", createIfNotExists: true);

        Assert.Equal("stay", opened.ReadAllText());
    }

    // === CreateDirectory / OpenDirectory ===

    [Fact]
    public void CreateDirectory_AddsSubdirectory()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("child");

        Assert.Equal("child", sub.Name);
        Assert.True(root.ItemExists("child"));
        Assert.Same(root, sub.Parent);
    }

    [Fact]
    public void CreateDirectory_InvalidName_Throws()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentException>(() => root.CreateDirectory(""));
        Assert.Throws<ArgumentException>(() => root.CreateDirectory("bad|name"));
    }

    [Fact]
    public void TryOpenDirectory_Existing_ReturnsTrue()
    {
        var root = NewRoot();
        root.CreateDirectory("sub");

        var found = root.TryOpenDirectory("sub", out var dir);

        Assert.True(found);
        Assert.NotNull(dir);
    }

    [Fact]
    public void TryOpenDirectory_Missing_ReturnsFalse()
    {
        var root = NewRoot();
        var found = root.TryOpenDirectory("x", out var dir);

        Assert.False(found);
        Assert.Null(dir);
    }

    [Fact]
    public void OpenDirectory_Missing_ThrowsDirectoryNotFound()
    {
        var root = NewRoot();
        Assert.Throws<DirectoryNotFoundException>(() => root.OpenDirectory("missing"));
    }

    [Fact]
    public void OpenDirectory_CreateIfNotExists_Creates()
    {
        var root = NewRoot();
        var dir = root.OpenDirectory("auto", createIfNotExists: true);

        Assert.NotNull(dir);
        Assert.True(root.ItemExists("auto"));
    }

    [Fact]
    public void OpenDirectory_NestedPath_NavigatesIntoSubdirectory()
    {
        var root = NewRoot();
        root.CreateDirectory("folder1").CreateDirectory("folder2");

        var opened = root.OpenDirectory("folder1/folder2");

        Assert.Equal("folder2", opened.Name);
    }

    [Fact]
    public void OpenDirectory_NestedPath_CreateIfNotExists_CreatesChain()
    {
        var root = NewRoot();

        var opened = root.OpenDirectory("folder1/folder2/folder3", createIfNotExists: true);

        Assert.Equal("folder3", opened.Name);
        Assert.True(root.TryOpenDirectory("folder1", out var f1));
        Assert.True(f1.TryOpenDirectory("folder2", out var f2));
        Assert.True(f2.TryOpenDirectory("folder3", out _));
    }

    [Fact]
    public void OpenFile_NestedPath_OpensFileInSubdirectory()
    {
        var root = NewRoot();
        root.CreateDirectory("folder1").CreateFile("file.txt").SetText("payload");

        var opened = root.OpenFile("folder1/file.txt");

        Assert.Equal("file.txt", opened.Name);
        Assert.Equal("payload", opened.ReadAllText());
    }

    [Fact]
    public void OpenDirectory_ParentTraversal_Throws()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("sub");

        Assert.Throws<FileHubException>(() => sub.OpenDirectory("../escape"));
    }

    [Fact]
    public void OpenFile_ParentTraversal_Throws()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("sub");

        Assert.Throws<FileHubException>(() => sub.OpenFile("../escape.txt"));
    }

    [Fact]
    public void OpenDirectory_LeadingSlash_Throws()
    {
        var root = NewRoot();
        root.CreateDirectory("folder1");

        Assert.Throws<FileHubException>(() => root.OpenDirectory("/folder1"));
        Assert.Throws<FileHubException>(() => root.OpenDirectory("\\folder1"));
    }

    [Fact]
    public void OpenFile_LeadingSlash_Throws()
    {
        var root = NewRoot();

        Assert.Throws<FileHubException>(() => root.OpenFile("/file.txt"));
        Assert.Throws<FileHubException>(() => root.OpenFile("\\file.txt"));
    }

    // === GetFiles / GetDirectories with patterns ===

    [Fact]
    public void GetFiles_NoPattern_ReturnsAll()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.log");

        var names = root.GetFiles().Select(f => f.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "a.txt", "b.log" }, names);
    }

    [Fact]
    public void GetFiles_OffsetAndLimit_PaginateSlice()
    {
        var root = NewRoot();
        for (int i = 0; i < 5; i++) root.CreateFile($"f{i}.txt");

        var first = root.GetFiles(offset: 0, limit: 2).ToArray();
        var second = root.GetFiles(offset: 2, limit: 2).ToArray();

        Assert.Equal(2, first.Length);
        Assert.Equal(2, second.Length);
        Assert.Empty(first.Select(f => f.Name).Intersect(second.Select(f => f.Name)));
    }

    [Fact]
    public void GetFiles_NegativeOffset_Throws()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentOutOfRangeException>(() => root.GetFiles(offset: -1).ToArray());
    }

    [Fact]
    public void GetFiles_NamedOffset_StartsFromCursorInclusive()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");
        root.CreateFile("c.txt");
        root.CreateFile("d.txt");

        var names = root.GetFiles(offset: FileListOffset.FromName("c.txt")).Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "c.txt", "d.txt" }, names);
    }

    [Fact]
    public void GetFiles_NamedOffsetWithLimit_PaginatesSlice()
    {
        var root = NewRoot();
        for (int i = 0; i < 5; i++) root.CreateFile($"f{i}.txt");

        var names = root.GetFiles(offset: FileListOffset.FromName("f2.txt"), limit: 2)
            .Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "f2.txt", "f3.txt" }, names);
    }

    [Fact]
    public void GetFiles_NegativeLimit_Throws()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentOutOfRangeException>(() => root.GetFiles(limit: -1).ToArray());
    }

    [Fact]
    public void GetFiles_StarDotStar_ReturnsAll()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.log");

        var names = root.GetFiles("*.*").Select(f => f.Name).ToArray();

        Assert.Equal(2, names.Length);
    }

    [Fact]
    public void GetFiles_SuffixPattern_FiltersByExtension()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");
        root.CreateFile("c.log");

        var names = root.GetFiles("*.txt").Select(f => f.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "a.txt", "b.txt" }, names);
    }

    [Fact]
    public void GetFiles_PrefixPattern_FiltersByPrefix()
    {
        var root = NewRoot();
        root.CreateFile("report_1.txt");
        root.CreateFile("report_2.txt");
        root.CreateFile("summary.txt");

        var names = root.GetFiles("report_*").Select(f => f.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "report_1.txt", "report_2.txt" }, names);
    }

    [Fact]
    public void GetFiles_ExactName_ReturnsOnlyMatching()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");

        var names = root.GetFiles("a.txt").Select(f => f.Name).ToArray();

        Assert.Single(names);
        Assert.Equal("a.txt", names[0]);
    }

    [Fact]
    public void GetDirectories_WithSuffixPattern_Filters()
    {
        var root = NewRoot();
        root.CreateDirectory("logs_2024");
        root.CreateDirectory("logs_2025");
        root.CreateDirectory("docs");

        var names = root.GetDirectories("*_2025").Select(d => d.Name).ToArray();

        Assert.Single(names);
        Assert.Equal("logs_2025", names[0]);
    }

    // === ItemExists / Exists ===

    [Fact]
    public void ItemExists_ReturnsTrueForFilesAndDirectories()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateDirectory("sub");

        Assert.True(root.ItemExists("a.txt"));
        Assert.True(root.ItemExists("sub"));
        Assert.False(root.ItemExists("other"));
    }

    [Fact]
    public void Exists_AfterDelete_ReturnsFalse()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("child");

        sub.Delete();

        Assert.False(sub.Exists());
    }

    // === Delete / DeleteIfExists ===

    [Fact]
    public void Delete_ByName_RemovesFile()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");

        root.Delete("a.txt");

        Assert.False(root.ItemExists("a.txt"));
    }

    [Fact]
    public void Delete_ByName_RemovesDirectory()
    {
        var root = NewRoot();
        root.CreateDirectory("sub");

        root.Delete("sub");

        Assert.False(root.ItemExists("sub"));
    }

    [Fact]
    public void Delete_Missing_ThrowsFileNotFound()
    {
        var root = NewRoot();
        Assert.Throws<FileNotFoundException>(() => root.Delete("ghost"));
    }

    [Fact]
    public void DeleteIfExists_MissingItem_NoOp()
    {
        var root = NewRoot();

        var ex = Record.Exception(() => root.DeleteIfExists("ghost"));

        Assert.Null(ex);
    }

    [Fact]
    public void DeleteIfExists_RemovesExisting()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");

        root.DeleteIfExists("a.txt");

        Assert.False(root.ItemExists("a.txt"));
    }

    // === Rename ===

    [Fact]
    public void Rename_ChangesName()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("old");
        sub.CreateFile("keep.txt").SetText("hi");

        var renamed = sub.Rename("new");

        Assert.Equal("new", renamed.Name);
        Assert.True(root.ItemExists("new"));
        Assert.False(root.ItemExists("old"));
        Assert.True(renamed.TryOpenFile("keep.txt", out _));
    }

    [Fact]
    public void Rename_InvalidName_Throws()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("a");
        Assert.Throws<ArgumentException>(() => sub.Rename(""));
    }

    // === MoveTo ===

    [Fact]
    public void MoveTo_MovesDirectory()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        var dst = root.CreateDirectory("dst");
        src.CreateFile("f.txt").SetText("x");

        var moved = src.MoveTo(dst, "moved");

        Assert.True(dst.ItemExists("moved"));
        Assert.False(root.ItemExists("src"));
        Assert.True(moved.TryOpenFile("f.txt", out _));
    }

    // === CopyTo ===

    [Fact]
    public void CopyTo_DeepCopiesContents()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        src.CreateFile("f.txt").SetText("hello");
        var inner = src.CreateDirectory("inner");
        inner.CreateFile("g.txt").SetText("world");

        var copy = src.CopyTo(root, "copy");

        Assert.True(root.ItemExists("copy"));
        Assert.True(copy.TryOpenFile("f.txt", out var f));
        Assert.Equal("hello", f.ReadAllText());
        Assert.True(copy.TryOpenDirectory("inner", out var innerCopy));
        Assert.True(innerCopy.TryOpenFile("g.txt", out var g));
        Assert.Equal("world", g.ReadAllText());
    }

    [Fact]
    public void CopyTo_OriginalStillExists()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        src.CreateFile("f.txt").SetText("data");

        src.CopyTo(root, "copy");

        Assert.True(src.Exists());
        Assert.True(src.TryOpenFile("f.txt", out _));
    }

    [Fact]
    public void CopyTo_AcrossDriverTypes_UsesGenericPath()
    {
        var memRoot = NewRoot();
        var src = memRoot.CreateDirectory("src");
        src.CreateFile("a.txt").SetText("payload");
        src.CreateDirectory("nested").CreateFile("b.txt").SetText("deep");

        var tempRoot = new TempDirectory();
        try
        {
            var localRoot = new FileHub.Local.LocalFileHub(tempRoot.Path).Root;

            var copied = src.CopyTo(localRoot, "copied");

            Assert.True(copied.TryOpenFile("a.txt", out var a));
            Assert.Equal("payload", a.ReadAllText());
            Assert.True(copied.TryOpenDirectory("nested", out var nested));
            Assert.True(nested.TryOpenFile("b.txt", out var b));
            Assert.Equal("deep", b.ReadAllText());
        }
        finally
        {
            tempRoot.Dispose();
        }
    }

    // === SetLastWriteTime ===

    [Fact]
    public void SetLastWriteTime_OnMemoryDirectory_IsNoOpButDoesNotThrow()
    {
        var root = NewRoot();
        var ex = Record.Exception(() => root.SetLastWriteTime(DateTime.UtcNow));
        Assert.Null(ex);
    }

    // === Async wrappers ===

    [Fact]
    public async Task CreateFileAsync_CreatesFile()
    {
        var root = NewRoot();
        var file = await root.CreateFileAsync("a.txt");

        Assert.NotNull(file);
        Assert.True(root.ItemExists("a.txt"));
    }

    [Fact]
    public async Task CreateFileAsync_WithOverwrite_Replaces()
    {
        var root = NewRoot();
        (await root.CreateFileAsync("a.txt")).SetText("old");

        var replaced = await root.CreateFileAsync("a.txt", overwrite: true);

        Assert.Equal(0, replaced.Length);
    }

    [Fact]
    public async Task OpenFileAsync_CreateIfNotExists_Creates()
    {
        var root = NewRoot();
        var file = await root.OpenFileAsync("a.txt", createIfNotExists: true);
        Assert.NotNull(file);
    }

    [Fact]
    public async Task OpenFileAsync_Missing_Throws()
    {
        var root = NewRoot();
        await Assert.ThrowsAsync<FileNotFoundException>(() => root.OpenFileAsync("x.txt"));
    }

    [Fact]
    public async Task CreateAndOpenDirectoryAsync_Works()
    {
        var root = NewRoot();
        var created = await root.CreateDirectoryAsync("d");
        Assert.NotNull(created);

        var opened = await root.OpenDirectoryAsync("d");
        Assert.NotNull(opened);

        var autoCreated = await root.OpenDirectoryAsync("d2", createIfNotExists: true);
        Assert.NotNull(autoCreated);
    }

    [Fact]
    public async Task ItemExistsAsync_Works()
    {
        var root = NewRoot();
        await root.CreateFileAsync("a.txt");

        Assert.True(await root.ItemExistsAsync("a.txt"));
        Assert.False(await root.ItemExistsAsync("b.txt"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var root = NewRoot();
        await root.CreateFileAsync("a.txt");

        await root.DeleteAsync("a.txt");

        Assert.False(root.ItemExists("a.txt"));
    }

    [Fact]
    public async Task DeleteIfExistsAsync_NoOpForMissing()
    {
        var root = NewRoot();
        await root.DeleteIfExistsAsync("ghost");
    }

    [Fact]
    public async Task RenameAsync_RenamesDirectory()
    {
        var root = NewRoot();
        var sub = root.CreateDirectory("old");

        var renamed = await sub.RenameAsync("new");

        Assert.Equal("new", renamed.Name);
    }

    [Fact]
    public async Task MoveToAsync_MovesDirectory()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        var dst = root.CreateDirectory("dst");

        var moved = await src.MoveToAsync(dst, "moved");

        Assert.True(dst.ItemExists("moved"));
        Assert.Equal("moved", moved.Name);
    }

    [Fact]
    public async Task CopyToAsync_CopiesDirectory()
    {
        var root = NewRoot();
        var src = root.CreateDirectory("src");
        src.CreateFile("a.txt").SetText("data");

        var copy = await src.CopyToAsync(root, "copy");

        Assert.True(copy.TryOpenFile("a.txt", out var f));
        Assert.Equal("data", f.ReadAllText());
    }

    [Fact]
    public async Task SetLastWriteTimeAsync_DoesNotThrow()
    {
        var root = NewRoot();
        await root.SetLastWriteTimeAsync(DateTime.UtcNow);
    }

    [Fact]
    public async Task GetFilesAsync_EnumeratesAllFiles()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");
        root.CreateFile("b.txt");

        var names = new System.Collections.Generic.List<string>();
        await foreach (var f in root.GetFilesAsync())
            names.Add(f.Name);

        Assert.Equal(2, names.Count);
    }

    [Fact]
    public async Task GetDirectoriesAsync_EnumeratesAllDirectories()
    {
        var root = NewRoot();
        root.CreateDirectory("a");
        root.CreateDirectory("b");

        var names = new System.Collections.Generic.List<string>();
        await foreach (var d in root.GetDirectoriesAsync())
            names.Add(d.Name);

        Assert.Equal(2, names.Count);
    }

    [Fact]
    public async Task GetFilesAsync_RespectsCancellation()
    {
        var root = NewRoot();
        root.CreateFile("a.txt");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in root.GetFilesAsync(cancellationToken: cts.Token))
            {
            }
        });
    }

    [Fact]
    public void ToString_ReturnsPath()
    {
        var hub = new MemoryFileHub("root");
        var sub = hub.Root.CreateDirectory("child");
        Assert.Equal(sub.Path, sub.ToString());
    }

    // === Timestamps ===

    [Fact]
    public void CreationTimeUtc_IsSet()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var root = NewRoot();

        Assert.InRange(root.CreationTimeUtc, before, DateTime.UtcNow.AddSeconds(1));
    }
}
