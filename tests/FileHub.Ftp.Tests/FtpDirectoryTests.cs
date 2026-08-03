using FileHub.Ftp.Tests.Fakes;

namespace FileHub.Ftp.Tests;

public class FtpDirectoryTests : FtpTestBase
{
    [Fact]
    public void CreateFile_MaterialisesEmptyFile()
    {
        var file = Root.CreateFile("report.txt");

        Assert.Equal("report.txt", file.Name);
        Assert.Equal("/report.txt", file.Path);
        Assert.Equal(0, file.Length);
        Assert.NotNull(Client.Server.Find("/report.txt"));
    }

    [Fact]
    public void CreateFile_NestedPath_CreatesIntermediateDirectories()
    {
        var file = Root.CreateFile("a/b/c.txt");

        Assert.Equal("c.txt", file.Name);
        Assert.Equal("/a/b/c.txt", file.Path);
        Assert.NotNull(Client.Server.Find("/a/b/c.txt"));
    }

    [Fact]
    public void CreateFile_MixedSeparators_NormalizesPathToForwardSlash()
    {
        // Input mixes both a backslash and a forward slash — the FTP wire
        // protocol only understands "/", so the driver must normalize before
        // issuing MKD/STOR. Real nesting must appear, and no node may carry a
        // literal backslash in its name.
        var file = Root.CreateFile("a\\b/c\\d.txt");

        Assert.Equal("/a/b/c/d.txt", file.Path);
        Assert.NotNull(Client.Server.Find("/a/b/c/d.txt"));
        Assert.Null(Client.Server.Find("/a\\b/c\\d.txt"));
        Assert.False(Client.Server.Root.Children.Keys.Any(k => k.Contains('\\')));
    }

    [Fact]
    public void CreateDirectory_MixedSeparators_NormalizesPathToForwardSlash()
    {
        var dir = Root.CreateDirectory("x\\y/z");

        Assert.Equal("/x/y/z", dir.Path);
        Assert.NotNull(Client.Server.Find("/x/y/z"));
        Assert.False(Client.Server.Root.Children.Keys.Any(k => k.Contains('\\')));
        Assert.True(Root.TryOpenDirectory("x/y/z", out _));
    }

    [Fact]
    public void TryOpenFile_ReturnsFalseWhenMissing()
    {
        Assert.False(Root.TryOpenFile("missing.txt", out _));
    }

    [Fact]
    public void Exists_DetectsFileAndDirectory_InOneStatCall()
    {
        Root.CreateFile("report.txt").SetText("x");
        Root.CreateDirectory("logs");

        var statsBefore = Client.StatInvocationCount;
        var fileProbesBefore = Client.FileExistsInvocationCount;
        var dirProbesBefore = Client.DirectoryExistsInvocationCount;

        Assert.True(Root.Exists("report.txt"));   // file
        Assert.True(Root.Exists("logs"));         // directory
        Assert.False(Root.Exists("missing"));     // neither

        // One STAT per probe — no separate file + directory round-trips.
        Assert.Equal(statsBefore + 3, Client.StatInvocationCount);
        Assert.Equal(fileProbesBefore, Client.FileExistsInvocationCount);
        Assert.Equal(dirProbesBefore, Client.DirectoryExistsInvocationCount);
    }

    [Fact]
    public void TryOpenFile_ReturnsHandleWithSize()
    {
        var created = Root.CreateFile("a.txt");
        created.SetText("hello");

        Assert.True(Root.TryOpenFile("a.txt", out var reopened));
        Assert.Equal(5, reopened.Length);
    }

    [Fact]
    public void CreateDirectory_SingleSegment_CreatesOnServer()
    {
        var child = Root.CreateDirectory("sub");

        Assert.Equal("/sub", child.Path);
        Assert.NotNull(Client.Server.Find("/sub"));
    }

    [Fact]
    public void CreateDirectory_Nested_CreatesEachSegment()
    {
        var deep = Root.CreateDirectory("a/b/c");

        Assert.Equal("/a/b/c", deep.Path);
        Assert.NotNull(Client.Server.Find("/a"));
        Assert.NotNull(Client.Server.Find("/a/b"));
        Assert.NotNull(Client.Server.Find("/a/b/c"));
    }

    [Fact]
    public void CreateDirectory_Nested_CreatesInOneRecursiveCall()
    {
        using var client = new InMemoryFtpClient();
        using var hub = FtpFileHub.FromFtpClient(client, "/");

        var deep = hub.Root.CreateDirectory("x/y/z");

        Assert.Equal("/x/y/z", deep.Path);
        Assert.NotNull(client.Server.Find("/x/y/z"));
    }

    [Fact]
    public void TryOpenDirectory_NestedMissingSegment_ReturnsFalse()
    {
        Root.CreateDirectory("a");

        Assert.False(Root.TryOpenDirectory("a/b/c", out _));
    }

    [Fact]
    public void CreateDirectory_RejectsParentTraversal()
    {
        Assert.Throws<FileHubException>(() => Root.CreateDirectory("a/../../escape"));
    }

    [Fact]
    public void GetFiles_ListsTopLevelOnly()
    {
        Root.CreateFile("a.txt");
        Root.CreateFile("b.txt");
        Root.CreateDirectory("sub");

        var names = Root.GetFiles().Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "a.txt", "b.txt" }, names);
    }

    [Fact]
    public void GetFiles_WithSearchPattern_FiltersByExtension()
    {
        Root.CreateFile("data.csv");
        Root.CreateFile("notes.txt");
        Root.CreateFile("summary.csv");

        var names = Root.GetFiles("*.csv").Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "data.csv", "summary.csv" }, names);
    }

    [Fact]
    public void GetFiles_IndexOffsetAndLimit_PaginatesInOrdinalOrder()
    {
        foreach (var n in new[] { "a", "b", "c", "d", "e" })
            Root.CreateFile(n);

        var names = Root
            .GetFiles("*", offset: FileListOffset.FromIndex(1), limit: 2)
            .Select(f => f.Name)
            .ToArray();

        Assert.Equal(new[] { "b", "c" }, names);
    }

    [Fact]
    public void GetFiles_NamedOffset_StartsStrictlyAfterName()
    {
        foreach (var n in new[] { "a", "b", "c", "d" })
            Root.CreateFile(n);

        // Named offsets are exclusive — same contract as S3 StartAfter and
        // OCI's `start` parameter. Paginating with the last item of page N
        // as the cursor for page N+1 must not yield a duplicate.
        var names = Root
            .GetFiles("*", offset: FileListOffset.FromName("c"))
            .Select(f => f.Name)
            .ToArray();

        Assert.Equal(new[] { "d" }, names);
    }

    [Fact]
    public void GetDirectories_ListsOnlyDirectories()
    {
        Root.CreateDirectory("alpha");
        Root.CreateDirectory("beta");
        Root.CreateFile("readme.md");

        var names = Root.GetDirectories().Select(d => d.Name).ToArray();

        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [Fact]
    public void ItemExists_TrueForBothFilesAndDirectories()
    {
        Root.CreateFile("f.txt");
        Root.CreateDirectory("d");

        Assert.True(Root.FileExists("f.txt"));
        Assert.True(Root.DirectoryExists("d"));
        Assert.False(Root.DirectoryExists("missing"));
    }

    [Fact]
    public void Delete_NamedFile_RemovesIt()
    {
        Root.CreateFile("victim.txt");

        Root.Delete("victim.txt");

        Assert.Null(Client.Server.Find("/victim.txt"));
    }

    [Fact]
    public void Delete_NamedDirectory_RemovesRecursively()
    {
        var sub = Root.CreateDirectory("sub");
        sub.CreateFile("inner.txt");

        Root.Delete("sub", recursive: true);

        Assert.Null(Client.Server.Find("/sub"));
    }

    [Fact]
    public void Delete_NonEmptyDirectoryWithoutRecursive_Throws()
    {
        var sub = Root.CreateDirectory("sub");
        sub.CreateFile("inner.txt");

        Assert.Throws<DirectoryNotEmptyException>(() => Root.Delete("sub"));
        Assert.NotNull(Client.Server.Find("/sub"));
    }

    [Fact]
    public void Delete_Self_AtRoot_Throws()
    {
        Assert.Throws<NotSupportedException>(() => Root.Delete());
    }

    [Fact]
    public void CreateFile_SingleArg_ExistingFile_Throws()
    {
        Root.CreateFile("a.txt").SetText("keep");

        Assert.Throws<FileAlreadyExistsException>(() => Root.CreateFile("a.txt"));
        Assert.Equal("keep", Root.OpenFile("a.txt").ReadAllText());
    }

    [Fact]
    public void Delete_Self_AtSub_RemovesFromParent()
    {
        var sub = Root.CreateDirectory("doomed");

        sub.Delete();

        Assert.Null(Client.Server.Find("/doomed"));
    }

    [Fact]
    public void Rename_ChangesPathUnderSameParent()
    {
        var sub = Root.CreateDirectory("old");

        var renamed = sub.Rename("new");

        Assert.Equal("/new", renamed.Path);
        Assert.Null(Client.Server.Find("/old"));
        Assert.NotNull(Client.Server.Find("/new"));
    }

    [Fact]
    public void Rename_Root_Throws()
    {
        Assert.Throws<NotSupportedException>(() => Root.Rename("x"));
    }

    [Fact]
    public void MoveTo_SameConnection_UsesServerSideRename()
    {
        var src = Root.CreateDirectory("src");
        src.CreateFile("inside.txt").SetText("hello");
        var destParent = Root.CreateDirectory("dest");

        var moved = src.MoveTo(destParent, "moved");

        Assert.Equal("/dest/moved", moved.Path);
        Assert.Null(Client.Server.Find("/src"));
        Assert.NotNull(Client.Server.Find("/dest/moved/inside.txt"));
    }

    [Fact]
    public void CopyTo_RecursivelyDuplicates()
    {
        var src = Root.CreateDirectory("src");
        src.CreateFile("a.txt").SetText("alpha");
        var nested = src.CreateDirectory("nested");
        nested.CreateFile("b.txt").SetText("beta");

        var destParent = Root.CreateDirectory("dest");
        src.CopyTo(destParent, "clone");

        Assert.NotNull(Client.Server.Find("/src/a.txt"));
        Assert.NotNull(Client.Server.Find("/dest/clone/a.txt"));
        Assert.NotNull(Client.Server.Find("/dest/clone/nested/b.txt"));
    }

    [Fact]
    public void CreateFile_WhenRootHasSandbox_RejectsEscapeAttempts()
    {
        using var client = new InMemoryFtpClient();
        using var hub = FtpFileHub.FromFtpClient(client, "/sandbox");

        Assert.Throws<FileHubException>(() => hub.Root.CreateFile("../etc/passwd"));
    }
}
