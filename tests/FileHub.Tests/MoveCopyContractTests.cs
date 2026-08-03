using System.Text;
using FileHub.Local;
using FileHub.Memory;

namespace FileHub.Tests;

/// <summary>
/// Cross-driver contract for the move/copy/delete business rules. The same
/// assertions run against every driver whose backend is available in-process
/// (Memory, Local), so a driver that diverges from the shared contract fails
/// here. Driver-specific backends (S3, OCI) assert the same self-move/self-copy
/// rule against their in-memory fakes in their own test projects.
/// </summary>
public abstract class MoveCopyContractTests : IDisposable
{
    protected abstract FileDirectory Root { get; }

    public virtual void Dispose() { }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // === Move (file) ===

    [Fact]
    public void MoveFile_MovesContentAndRemovesSource()
    {
        var file = Root.CreateFile("a.txt", Bytes("hello"));

        var moved = file.MoveTo(Root, "b.txt");

        Assert.False(Root.FileExists("a.txt"));
        Assert.True(Root.FileExists("b.txt"));
        Assert.Equal("hello", moved.ReadAllText());
        Assert.Equal("hello", Root.OpenFile("b.txt").ReadAllText());
    }

    [Fact]
    public void MoveFile_IntoSubdirectory()
    {
        var file = Root.CreateFile("a.txt", Bytes("hi"));
        var sub = Root.CreateDirectory("sub");

        var moved = file.MoveTo(sub, "a.txt");

        Assert.False(Root.FileExists("a.txt"));
        Assert.True(sub.FileExists("a.txt"));
        Assert.Equal("hi", moved.ReadAllText());
    }

    [Fact]
    public void MoveFile_OverwriteFalse_OntoExisting_Throws()
    {
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        Assert.Throws<FileAlreadyExistsException>(() => b.MoveTo(Root, "a.txt", overwrite: false));

        // Both survive — the failed move must not have deleted the source.
        Assert.True(Root.FileExists("a.txt"));
        Assert.True(Root.FileExists("b.txt"));
    }

    [Fact]
    public void MoveFile_DefaultOverwrite_OntoExisting_Throws()
    {
        // overwrite defaults to false (BCL File.Move parity) — an existing
        // destination must not be clobbered when the caller omits the flag.
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        Assert.Throws<FileAlreadyExistsException>(() => b.MoveTo(Root, "a.txt"));

        Assert.Equal("1", Root.OpenFile("a.txt").ReadAllText());
        Assert.True(Root.FileExists("b.txt"));
    }

    [Fact]
    public async Task MoveFileAsync_DefaultOverwrite_OntoExisting_Throws()
    {
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => b.MoveToAsync(Root, "a.txt"));

        Assert.Equal("1", Root.OpenFile("a.txt").ReadAllText());
        Assert.True(Root.FileExists("b.txt"));
    }

    [Fact]
    public void MoveFile_OverwriteTrue_ReplacesTarget()
    {
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        b.MoveTo(Root, "a.txt", overwrite: true);

        Assert.False(Root.FileExists("b.txt"));
        Assert.Equal("2", Root.OpenFile("a.txt").ReadAllText());
    }

    [Fact]
    public void MoveFile_OntoItself_Throws_AndKeepsFile()
    {
        var file = Root.CreateFile("a.txt", Bytes("keep"));

        Assert.Throws<FileAlreadyExistsException>(() => file.MoveTo(Root, "a.txt"));

        Assert.True(Root.FileExists("a.txt"));
        Assert.Equal("keep", Root.OpenFile("a.txt").ReadAllText());
    }

    [Fact]
    public async Task MoveFileAsync_OntoItself_Throws_AndKeepsFile()
    {
        var file = Root.CreateFile("a.txt", Bytes("keep"));

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => file.MoveToAsync(Root, "a.txt"));

        Assert.True(Root.FileExists("a.txt"));
    }

    // === Copy (file) ===

    [Fact]
    public void CopyFile_KeepsSourceAndDuplicatesContent()
    {
        var file = Root.CreateFile("a.txt", Bytes("hello"));

        var copy = file.CopyTo(Root, "b.txt");

        Assert.True(Root.FileExists("a.txt"));
        Assert.Equal("hello", Root.OpenFile("a.txt").ReadAllText());
        Assert.Equal("hello", copy.ReadAllText());
    }

    [Fact]
    public void CopyFile_OverwriteFalse_OntoExisting_Throws()
    {
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        Assert.Throws<FileAlreadyExistsException>(() => b.CopyTo(Root, "a.txt", overwrite: false));
    }

    [Fact]
    public void CopyFile_DefaultOverwrite_OntoExisting_Throws()
    {
        // overwrite defaults to false (BCL File.Copy parity) — an existing
        // destination must not be clobbered when the caller omits the flag.
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        Assert.Throws<FileAlreadyExistsException>(() => b.CopyTo(Root, "a.txt"));

        Assert.Equal("1", Root.OpenFile("a.txt").ReadAllText());
    }

    [Fact]
    public async Task CopyFileAsync_DefaultOverwrite_OntoExisting_Throws()
    {
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => b.CopyToAsync(Root, "a.txt"));

        Assert.Equal("1", Root.OpenFile("a.txt").ReadAllText());
    }

    [Fact]
    public void CopyFile_OverwriteTrue_ReplacesTarget()
    {
        Root.CreateFile("a.txt", Bytes("1"));
        var b = Root.CreateFile("b.txt", Bytes("2"));

        b.CopyTo(Root, "a.txt", overwrite: true);

        Assert.Equal("2", Root.OpenFile("a.txt").ReadAllText());
        Assert.True(Root.FileExists("b.txt"));
    }

    [Fact]
    public void CopyFile_OntoItself_Throws_AndKeepsFile()
    {
        var file = Root.CreateFile("a.txt", Bytes("keep"));

        Assert.Throws<FileAlreadyExistsException>(() => file.CopyTo(Root, "a.txt"));

        Assert.True(Root.FileExists("a.txt"));
        Assert.Equal("keep", Root.OpenFile("a.txt").ReadAllText());
    }

    // === Move / Copy (directory) ===

    [Fact]
    public void MoveDirectory_MovesContentsAndRemovesSource()
    {
        var dir = Root.CreateDirectory("d1");
        dir.CreateFile("f.txt", Bytes("data"));

        Root.OpenDirectory("d1").MoveTo(Root, "d2");

        Assert.False(Root.DirectoryExists("d1"));
        Assert.True(Root.DirectoryExists("d2"));
        Assert.Equal("data", Root.OpenFile("d2/f.txt").ReadAllText());
    }

    [Fact]
    public void MoveDirectory_DefaultOverwrite_OntoExisting_Throws()
    {
        // Directory move mirrors Directory.Move — an existing destination is a
        // throw, never a silent merge, when the flag is omitted.
        var src = Root.CreateDirectory("d1");
        src.CreateFile("f.txt", Bytes("new"));
        var dst = Root.CreateDirectory("d2");
        dst.CreateFile("f.txt", Bytes("old"));

        Assert.Throws<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").MoveTo(Root, "d2"));

        // Source untouched — the failed move must not have deleted it.
        Assert.Equal("new", Root.OpenFile("d1/f.txt").ReadAllText());
        Assert.Equal("old", Root.OpenFile("d2/f.txt").ReadAllText());
    }

    [Fact]
    public async Task MoveDirectoryAsync_DefaultOverwrite_OntoExisting_Throws()
    {
        var src = Root.CreateDirectory("d1");
        src.CreateFile("f.txt", Bytes("new"));
        Root.CreateDirectory("d2").CreateFile("f.txt", Bytes("old"));

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").MoveToAsync(Root, "d2"));

        Assert.True(Root.DirectoryExists("d1"));
        Assert.Equal("old", Root.OpenFile("d2/f.txt").ReadAllText());
    }

    [Fact]
    public void MoveDirectory_OverwriteTrue_MergesAndRemovesSource()
    {
        var src = Root.CreateDirectory("d1");
        src.CreateFile("shared.txt", Bytes("new"));
        src.CreateFile("only-src.txt", Bytes("s"));
        var dst = Root.CreateDirectory("d2");
        dst.CreateFile("shared.txt", Bytes("old"));
        dst.CreateFile("only-dst.txt", Bytes("d"));

        Root.OpenDirectory("d1").MoveTo(Root, "d2", overwrite: true);

        Assert.False(Root.DirectoryExists("d1"));                            // source removed
        Assert.Equal("new", Root.OpenFile("d2/shared.txt").ReadAllText());   // colliding leaf replaced
        Assert.Equal("s", Root.OpenFile("d2/only-src.txt").ReadAllText());   // source-only added
        Assert.Equal("d", Root.OpenFile("d2/only-dst.txt").ReadAllText());   // destination-only kept
    }

    [Fact]
    public void CopyDirectory_DuplicatesTreeAndKeepsSource()
    {
        var dir = Root.CreateDirectory("d1");
        dir.CreateFile("f.txt", Bytes("data"));

        Root.OpenDirectory("d1").CopyTo(Root, "d2");

        Assert.Equal("data", Root.OpenFile("d1/f.txt").ReadAllText());
        Assert.Equal("data", Root.OpenFile("d2/f.txt").ReadAllText());
    }

    [Fact]
    public void CopyDirectory_DefaultOverwrite_OntoExisting_Throws()
    {
        // Default overwrite is false — an existing destination directory must
        // not be clobbered/merged into when the flag is omitted.
        var src = Root.CreateDirectory("d1");
        src.CreateFile("f.txt", Bytes("new"));
        var dst = Root.CreateDirectory("d2");
        dst.CreateFile("f.txt", Bytes("old"));

        Assert.Throws<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").CopyTo(Root, "d2"));

        // Destination untouched — nothing half-copied.
        Assert.Equal("old", Root.OpenFile("d2/f.txt").ReadAllText());
    }

    [Fact]
    public void CopyDirectory_OverwriteTrue_MergesAndReplacesCollisions()
    {
        var src = Root.CreateDirectory("d1");
        src.CreateFile("shared.txt", Bytes("new"));
        src.CreateFile("only-src.txt", Bytes("s"));
        var dst = Root.CreateDirectory("d2");
        dst.CreateFile("shared.txt", Bytes("old"));
        dst.CreateFile("only-dst.txt", Bytes("d"));

        Root.OpenDirectory("d1").CopyTo(Root, "d2", overwrite: true);

        Assert.Equal("new", Root.OpenFile("d2/shared.txt").ReadAllText()); // colliding leaf replaced
        Assert.Equal("s", Root.OpenFile("d2/only-src.txt").ReadAllText()); // source-only added
        Assert.True(Root.FileExists("d1/shared.txt"));                     // source kept
    }

    [Fact]
    public void MoveDirectory_OntoItself_Throws_AndKeepsDirectory()
    {
        var dir = Root.CreateDirectory("d1");
        dir.CreateFile("f.txt", Bytes("data"));

        Assert.Throws<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").MoveTo(Root, "d1"));

        Assert.True(Root.DirectoryExists("d1"));
        Assert.Equal("data", Root.OpenFile("d1/f.txt").ReadAllText());
    }

    [Fact]
    public void CopyDirectory_OntoItself_Throws()
    {
        var dir = Root.CreateDirectory("d1");
        dir.CreateFile("f.txt", Bytes("data"));

        Assert.Throws<FileAlreadyExistsException>(() => Root.OpenDirectory("d1").CopyTo(Root, "d1"));

        Assert.True(Root.DirectoryExists("d1"));
    }

    [Fact]
    public void MoveDirectory_IntoOwnDescendant_Throws()
    {
        var outer = Root.CreateDirectory("outer");
        var inner = outer.CreateDirectory("inner");

        Assert.Throws<FileHubException>(() => outer.MoveTo(inner, "moved"));

        Assert.True(Root.DirectoryExists("outer"));
        Assert.True(Root.DirectoryExists("outer/inner"));
    }

    [Fact]
    public void CopyDirectory_IntoOwnDescendant_Throws()
    {
        var outer = Root.CreateDirectory("outer");
        var inner = outer.CreateDirectory("inner");

        Assert.Throws<FileHubException>(() => outer.CopyTo(inner, "copied"));

        Assert.True(Root.DirectoryExists("outer"));
    }
}

public sealed class MemoryMoveCopyContractTests : MoveCopyContractTests
{
    private readonly MemoryFileHub _hub = new();
    protected override FileDirectory Root => _hub.Root;
}

public sealed class LocalMoveCopyContractTests : MoveCopyContractTests
{
    private readonly TempDirectory _temp = new();
    private readonly LocalFileHub _hub;

    public LocalMoveCopyContractTests()
    {
        _hub = new LocalFileHub(_temp.Path);
    }

    protected override FileDirectory Root => _hub.Root;

    public override void Dispose() => _temp.Dispose();
}
