using System;
using System.IO;
using System.Linq;
using FileHub.OracleObjectStorage.Internal;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageDirectoryTests : IClassFixture<InMemoryOciFixture>
{
    private readonly InMemoryOciFixture _fixture;
    private FileDirectory Root => _fixture.FileHub.Root;

    public OracleObjectStorageDirectoryTests(InMemoryOciFixture fixture) => _fixture = fixture;

    private FileDirectory Scope(string name) => Root.OpenDirectory(name, createIfNotExists: true);

    [Fact]
    public void CreateDirectory_CreatesMarkerObject()
    {
        var scope = Scope(nameof(CreateDirectory_CreatesMarkerObject));
        var child = scope.CreateDirectory("alpha");

        Assert.NotNull(child);
        Assert.Equal("alpha", child.Name);
        Assert.True(scope.DirectoryExists("alpha"));
        Assert.True(_fixture.Client.TryGetBody($"{nameof(CreateDirectory_CreatesMarkerObject)}/alpha/", out _));
    }

    [Fact]
    public void CreateFile_EmptyObject_AppearsInGetFiles()
    {
        var scope = Scope(nameof(CreateFile_EmptyObject_AppearsInGetFiles));
        scope.CreateFile("empty.txt");

        var files = scope.GetFiles().Select(f => f.Name).ToArray();
        Assert.Contains("empty.txt", files);
    }

    [Fact]
    public void GetFiles_HonorsWildcardPattern()
    {
        var scope = Scope(nameof(GetFiles_HonorsWildcardPattern));
        scope.CreateFile("a.txt");
        scope.CreateFile("b.txt");
        scope.CreateFile("c.log");

        Assert.Equal(new[] { "a.txt", "b.txt" },
            scope.GetFiles("*.txt").Select(f => f.Name).OrderBy(n => n).ToArray());

        Assert.Equal(new[] { "c.log" },
            scope.GetFiles("*.log").Select(f => f.Name).ToArray());
    }

    [Fact]
    public void GetDirectories_ReturnsOnlyImmediateChildren()
    {
        var scope = Scope(nameof(GetDirectories_ReturnsOnlyImmediateChildren));
        var a = scope.CreateDirectory("a");
        a.CreateDirectory("nested");
        scope.CreateDirectory("b");

        var names = scope.GetDirectories().Select(d => d.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "a", "b" }, names);
    }

    [Fact]
    public void ItemExists_ReturnsFalseForMissing()
    {
        var scope = Scope(nameof(ItemExists_ReturnsFalseForMissing));
        Assert.False(scope.DirectoryExists("missing"));
    }

    [Fact]
    public void Delete_Recursive_DeletesAllObjects()
    {
        var scope = Scope(nameof(Delete_Recursive_DeletesAllObjects));
        var sub = scope.CreateDirectory("to-delete");
        sub.CreateFile("a.txt").SetText("A");
        sub.CreateDirectory("nested").CreateFile("b.txt").SetText("B");

        sub.Delete();

        Assert.False(scope.DirectoryExists("to-delete"));
        Assert.Empty(_fixture.Client.Keys.Where(k => k.StartsWith($"{nameof(Delete_Recursive_DeletesAllObjects)}/to-delete/")));
    }

    [Fact]
    public void Delete_Root_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => Root.Delete());
    }

    [Fact]
    public void Rename_Directory_CopiesAllAndDeletesOriginal()
    {
        var scope = Scope(nameof(Rename_Directory_CopiesAllAndDeletesOriginal));
        var original = scope.CreateDirectory("before");
        original.CreateFile("one.txt").SetText("1");
        original.CreateFile("two.txt").SetText("2");

        var renamed = original.Rename("after");

        Assert.False(scope.DirectoryExists("before"));
        Assert.True(scope.DirectoryExists("after"));
        Assert.Equal(2, renamed.GetFiles().Count());
        Assert.Equal("1", renamed.OpenFile("one.txt").ReadAllText());
        Assert.Equal("2", renamed.OpenFile("two.txt").ReadAllText());
    }

    [Fact]
    public void GetFiles_IndexOffsetAndLimit_PaginateSlice()
    {
        var scope = Scope(nameof(GetFiles_IndexOffsetAndLimit_PaginateSlice));
        for (int i = 0; i < 10; i++)
            scope.CreateFile($"f{i:D2}.txt").SetBytes(new byte[] { (byte)i });

        var first = scope.GetFiles(offset: 0, limit: 3).Select(f => f.Name).ToArray();
        var second = scope.GetFiles(offset: 3, limit: 3).Select(f => f.Name).ToArray();

        Assert.Equal(3, first.Length);
        Assert.Equal(3, second.Length);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void GetFiles_NamedOffset_StartsFromCursorInclusive()
    {
        var scope = Scope(nameof(GetFiles_NamedOffset_StartsFromCursorInclusive));
        for (int i = 0; i < 5; i++)
            scope.CreateFile($"f{i:D2}.txt").SetBytes(new byte[] { (byte)i });

        var names = scope.GetFiles(offset: FileListOffset.FromName("f02.txt"), limit: 2)
            .Select(f => f.Name).ToArray();

        Assert.Equal(new[] { "f02.txt", "f03.txt" }, names);
    }

    [Fact]
    public void GetFiles_HandlesPagination()
    {
        var scope = Scope(nameof(GetFiles_HandlesPagination));
        // Simulate enough files to force multi-page enumeration.
        for (int i = 0; i < 50; i++)
            scope.CreateFile($"file{i:D3}.bin").SetBytes(new byte[] { (byte)i });

        var found = scope.GetFiles("*.bin").Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(50, found.Length);
    }

    [Fact]
    public void TryOpenFile_MissingName_ReturnsFalse()
    {
        var scope = Scope(nameof(TryOpenFile_MissingName_ReturnsFalse));
        Assert.False(scope.TryOpenFile("nope.txt", out var _));
    }

    [Fact]
    public void TryOpenDirectory_MissingName_ReturnsFalse()
    {
        var scope = Scope(nameof(TryOpenDirectory_MissingName_ReturnsFalse));
        Assert.False(scope.TryOpenDirectory("nope", out var _));
    }

    [Fact]
    public void CreateFile_WithInvalidName_Throws()
    {
        var scope = Scope(nameof(CreateFile_WithInvalidName_Throws));
        Assert.Throws<FileHubException>(() => scope.CreateFile(".."));
    }

    [Fact]
    public void CreateFile_NestedPath_CreatesIntermediateDirectories()
    {
        var scope = Scope(nameof(CreateFile_NestedPath_CreatesIntermediateDirectories));
        var file = scope.CreateFile("a/b/c.txt");

        Assert.Equal("c.txt", file.Name);
        Assert.True(scope.TryOpenFile("a/b/c.txt", out var reopened));
        Assert.Equal(0, reopened.Length);
    }

    // === Nested-path directory creation / lookup ===

    [Fact]
    public void CreateDirectory_ForwardSlash_CreatesIntermediate()
    {
        var scope = Scope(nameof(CreateDirectory_ForwardSlash_CreatesIntermediate));

        var leaf = scope.CreateDirectory("a/b/c");

        Assert.Equal("c", leaf.Name);
        Assert.True(scope.TryOpenDirectory("a", out _));
        Assert.True(scope.TryOpenDirectory("a/b", out _));
        Assert.True(scope.TryOpenDirectory("a/b/c", out _));
    }

    [Fact]
    public void CreateDirectory_Backslash_CreatesIntermediate()
    {
        var scope = Scope(nameof(CreateDirectory_Backslash_CreatesIntermediate));

        scope.CreateDirectory("x\\y");

        Assert.True(scope.TryOpenDirectory("x/y", out _));
    }

    [Fact]
    public void CreateDirectory_Nested_ReusesExistingIntermediate()
    {
        var scope = Scope(nameof(CreateDirectory_Nested_ReusesExistingIntermediate));
        var firstA = scope.CreateDirectory("a");
        firstA.CreateFile("keep.txt");

        scope.CreateDirectory("a/b");

        Assert.True(scope.TryOpenDirectory("a", out var reopenedA));
        Assert.True(reopenedA.FileExists("keep.txt"));
        Assert.True(scope.TryOpenDirectory("a/b", out _));
    }

    [Fact]
    public void TryOpenDirectory_NestedPath_ReturnsFalseWhenIntermediateMissing()
    {
        var scope = Scope(nameof(TryOpenDirectory_NestedPath_ReturnsFalseWhenIntermediateMissing));
        Assert.False(scope.TryOpenDirectory("missing/child", out var dir));
        Assert.Null(dir);
    }

    [Fact]
    public void CreateDirectory_AbsolutePath_Throws()
    {
        var scope = Scope(nameof(CreateDirectory_AbsolutePath_Throws));
        Assert.Throws<FileHubException>(() => scope.CreateDirectory("/abs"));
        Assert.Throws<FileHubException>(() => scope.CreateDirectory("\\abs"));
    }

    [Fact]
    public void CreateDirectory_ParentTraversal_Throws()
    {
        var scope = Scope(nameof(CreateDirectory_ParentTraversal_Throws));
        Assert.Throws<FileHubException>(() => scope.CreateDirectory("../escape"));
        Assert.Throws<FileHubException>(() => scope.CreateDirectory("a/../escape"));
    }

    // === DirectoryPathMode: Direct vs OpenIntermediates ===

    [Fact]
    public void CreateDirectory_DirectMode_CreatesOnlyLeafMarker()
    {
        // Default for OCI is Direct — only the leaf prefix is PUT.
        using var client = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(client);

        hub.Root.CreateDirectory("a/b/c");

        Assert.Equal(1, client.ObjectCount);
        Assert.Contains("a/b/c/", client.Keys);
        Assert.DoesNotContain("a/", client.Keys);
        Assert.DoesNotContain("a/b/", client.Keys);
    }

    [Fact]
    public void CreateDirectory_OpenIntermediatesMode_CreatesEachMarker()
    {
        using var client = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(client, "", DirectoryPathMode.OpenIntermediates);

        hub.Root.CreateDirectory("a/b/c");

        Assert.Equal(3, client.ObjectCount);
        Assert.Contains("a/", client.Keys);
        Assert.Contains("a/b/", client.Keys);
        Assert.Contains("a/b/c/", client.Keys);
    }

    [Fact]
    public void TryOpenDirectory_DirectMode_ResolvesExistingNestedPath()
    {
        using var client = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(client);

        hub.Root.CreateDirectory("a/b/c");

        Assert.True(hub.Root.TryOpenDirectory("a/b/c", out var reopened));
        Assert.Equal("c", reopened.Name);
    }

    [Fact]
    public void TryOpenDirectory_DirectMode_MissingPath_ReturnsFalse()
    {
        using var client = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(client);

        Assert.False(hub.Root.TryOpenDirectory("x/y/z", out var dir));
        Assert.Null(dir);
    }

    [Fact]
    public void CreateDirectory_DirectMode_ParentTraversal_Throws()
    {
        using var client = new InMemoryOciClient();
        using var hub = OracleObjectStorageFileHub.FromOciClient(client);

        Assert.Throws<FileHubException>(() => hub.Root.CreateDirectory("a/../escape"));
    }
}
