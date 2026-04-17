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
        Assert.True(scope.ItemExists("alpha"));
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
        Assert.False(scope.ItemExists("missing"));
    }

    [Fact]
    public void Delete_Recursive_DeletesAllObjects()
    {
        var scope = Scope(nameof(Delete_Recursive_DeletesAllObjects));
        var sub = scope.CreateDirectory("to-delete");
        sub.CreateFile("a.txt").SetText("A");
        sub.CreateDirectory("nested").CreateFile("b.txt").SetText("B");

        sub.Delete();

        Assert.False(scope.ItemExists("to-delete"));
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

        Assert.False(scope.ItemExists("before"));
        Assert.True(scope.ItemExists("after"));
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
        Assert.Throws<ArgumentException>(() => scope.CreateFile("a/b.txt"));
        Assert.Throws<ArgumentException>(() => scope.CreateFile(".."));
    }
}
