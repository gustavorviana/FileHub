using FileHub.Memory;

namespace FileHub.Tests;

/// <summary>
/// Tests for the behavior defined in the FileSystemEntry base class —
/// name/read-only handling, async defaults, dispose. Exercised through
/// the MemoryFileHub concrete types.
/// </summary>
public class FileSystemEntryTests
{
    private static FileDirectory NewRoot() => new MemoryFileHub().Root;

    [Fact]
    public void ValidateName_NullOrEmpty_Throws()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentException>(() => root.CreateFile(null));
        Assert.Throws<ArgumentException>(() => root.CreateFile(""));
        Assert.Throws<ArgumentException>(() => root.CreateDirectory(null));
        Assert.Throws<ArgumentException>(() => root.CreateDirectory(""));
    }

    [Theory]
    [InlineData("a:b.txt")]
    [InlineData("a|b.txt")]
    [InlineData("a?b.txt")]
    [InlineData("a*b.txt")]
    public void ValidateName_InvalidCharacters_Throws(string name)
    {
        var root = NewRoot();
        Assert.Throws<ArgumentException>(() => root.CreateFile(name));
    }

    [Theory]
    [InlineData("a/b.txt")]
    [InlineData("a\\b.txt")]
    public void CreateFile_NestedPath_CreatesIntermediateDirectories(string name)
    {
        var root = NewRoot();
        var file = root.CreateFile(name);
        Assert.Equal("b.txt", file.Name);
    }

    [Fact]
    public void IsReadOnly_DefaultsToFalse()
    {
        var root = NewRoot();
        Assert.False(root.IsReadOnly);
        var file = root.CreateFile("a.txt");
        Assert.False(file.IsReadOnly);
    }

    [Fact]
    public async Task ExistsAsync_DelegatesToSync()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");

        Assert.True(await file.ExistsAsync());
        file.Delete();
        Assert.False(await file.ExistsAsync());
    }

    [Fact]
    public async Task ExistsAsync_Canceled_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => file.ExistsAsync(cts.Token));
    }

    [Fact]
    public void Dispose_MarksEntryDisposed()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");

        file.Dispose();

        // Exists check on memory file returns false after dispose
        Assert.False(file.Exists());
    }

    [Fact]
    public void Name_UpdatedAfterRename()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.Rename("b.txt");
        Assert.Equal("b.txt", file.Name);
    }
}
