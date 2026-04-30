using FileHub.Local;
using FileHub.Memory;

namespace FileHub.Tests;

/// <summary>
/// Cross-driver checks that any method accepting a relative <c>name</c> tolerates
/// (a) a trailing <c>/</c> or <c>\</c> and (b) a nested subpath like
/// <c>"a/b/c.txt"</c>. Memory and Local share the same path-handling code in
/// <see cref="FileDirectory.SplitPath"/> / <see cref="NestedPath.TrySplit"/>, so
/// validating both here proves the invariant for every driver that delegates
/// through the same helpers.
/// </summary>
public class PathSemanticsTests
{
    public static IEnumerable<object[]> Roots()
    {
        yield return new object[] { (Func<IDisposable?, FileDirectory>)(_ => new MemoryFileHub().Root), (Func<IDisposable?>)(() => null) };
    }

    private static FileDirectory NewMemory() => new MemoryFileHub().Root;
    private static (FileDirectory Root, TempDirectory Temp) NewLocal()
    {
        var temp = new TempDirectory();
        return (new LocalFileHub(temp.Path).Root, temp);
    }

    // === Trailing separator on directories ===

    [Fact]
    public void Memory_TryOpenDirectory_AcceptsTrailingForwardSlash()
    {
        var root = NewMemory();
        root.CreateDirectory("foo");

        Assert.True(root.TryOpenDirectory("foo/", out var dir));
        Assert.NotNull(dir);
        Assert.Equal("foo", dir!.Name);
    }

    [Fact]
    public void Memory_TryOpenDirectory_AcceptsTrailingBackslash()
    {
        var root = NewMemory();
        root.CreateDirectory("foo");

        Assert.True(root.TryOpenDirectory("foo\\", out _));
    }

    [Fact]
    public void Memory_CreateDirectory_StripsTrailingSeparator()
    {
        var root = NewMemory();

        var dir = root.CreateDirectory("alpha/");

        Assert.Equal("alpha", dir.Name);
        Assert.True(root.DirectoryExists("alpha"));
    }

    [Fact]
    public void Local_TryOpenDirectory_AcceptsTrailingSeparator()
    {
        var (root, temp) = NewLocal();
        using var t = temp;
        root.CreateDirectory("foo");

        Assert.True(root.TryOpenDirectory("foo/", out var d1));
        Assert.NotNull(d1);
        Assert.True(root.TryOpenDirectory("foo\\", out var d2));
        Assert.NotNull(d2);
    }

    [Fact]
    public void Local_CreateDirectory_AcceptsTrailingSeparator()
    {
        var (root, temp) = NewLocal();
        using var t = temp;

        var dir = root.CreateDirectory("beta\\");

        Assert.Equal("beta", dir.Name);
        Assert.True(root.DirectoryExists("beta/"));
    }

    // === Nested paths in TryOpenDirectory / OpenDirectory ===

    [Fact]
    public void Memory_TryOpenDirectory_NestedWithTrailingSeparator()
    {
        var root = NewMemory();
        root.CreateDirectory("a/b/c");

        Assert.True(root.TryOpenDirectory("a/b/c/", out var leaf));
        Assert.Equal("c", leaf!.Name);
    }

    [Fact]
    public void Local_OpenDirectory_NestedWithMixedSeparators()
    {
        var (root, temp) = NewLocal();
        using var t = temp;
        root.CreateDirectory("a/b/c");

        var dir = root.OpenDirectory("a\\b/c\\");

        Assert.Equal("c", dir.Name);
    }

    // === FileExists / DirectoryExists with nested paths ===

    [Fact]
    public void Memory_FileExists_AcceptsNestedPath()
    {
        var root = NewMemory();
        root.CreateFile("a/b/c.txt").SetText("hi");

        Assert.True(root.FileExists("a/b/c.txt"));
        Assert.False(root.FileExists("a/b/missing.txt"));
        Assert.False(root.FileExists("a/missing/c.txt"));
    }

    [Fact]
    public void Memory_DirectoryExists_AcceptsNestedPathAndTrailingSeparator()
    {
        var root = NewMemory();
        root.CreateDirectory("a/b/c");

        Assert.True(root.DirectoryExists("a/b/c"));
        Assert.True(root.DirectoryExists("a/b/c/"));
        Assert.True(root.DirectoryExists("a\\b\\c\\"));
        Assert.False(root.DirectoryExists("a/b/missing"));
    }

    [Fact]
    public void Local_FileExists_AcceptsNestedPath()
    {
        var (root, temp) = NewLocal();
        using var t = temp;
        root.CreateFile("docs/2026/report.txt").SetText("x");

        Assert.True(root.FileExists("docs/2026/report.txt"));
        Assert.False(root.FileExists("docs/2026/other.txt"));
    }

    [Fact]
    public void Local_DirectoryExists_AcceptsNestedPath()
    {
        var (root, temp) = NewLocal();
        using var t = temp;
        root.CreateDirectory("docs/2026/q1");

        Assert.True(root.DirectoryExists("docs/2026/q1"));
        Assert.True(root.DirectoryExists("docs/2026/q1/"));
        Assert.False(root.DirectoryExists("docs/2025/q1"));
    }

    // === Delete with nested paths ===

    [Fact]
    public void Memory_Delete_AcceptsNestedPath()
    {
        var root = NewMemory();
        root.CreateFile("a/b/c.txt").SetText("hi");

        root.Delete("a/b/c.txt");

        Assert.False(root.FileExists("a/b/c.txt"));
        Assert.True(root.DirectoryExists("a/b"));
    }

    [Fact]
    public void Memory_Delete_NestedDirectoryWithTrailingSeparator()
    {
        var root = NewMemory();
        root.CreateDirectory("a/b/c");

        root.Delete("a/b/c/");

        Assert.False(root.DirectoryExists("a/b/c"));
        Assert.True(root.DirectoryExists("a/b"));
    }

    [Fact]
    public void Local_Delete_AcceptsNestedPath()
    {
        var (root, temp) = NewLocal();
        using var t = temp;
        root.CreateFile("docs/2026/q1.txt").SetText("x");

        root.Delete("docs/2026/q1.txt");

        Assert.False(root.FileExists("docs/2026/q1.txt"));
    }

    [Fact]
    public void Memory_Delete_NestedMissing_Throws()
    {
        var root = NewMemory();

        Assert.Throws<FileNotFoundException>(() => root.Delete("a/b/nope.txt"));
    }

    [Fact]
    public void DeleteIfExists_AcceptsNestedPath()
    {
        var root = NewMemory();
        root.CreateFile("a/b/c.txt").SetText("x");

        root.DeleteIfExists("a/b/c.txt");
        root.DeleteIfExists("a/b/already-gone.txt"); // no throw

        Assert.False(root.FileExists("a/b/c.txt"));
    }

    // === Invalid paths still rejected ===

    [Fact]
    public void TrailingSeparatorOnly_StillRejectedAsAbsolute()
    {
        var root = NewMemory();

        Assert.Throws<FileHubException>(() => root.TryOpenDirectory("/", out _));
    }

    [Fact]
    public void ParentTraversal_StillRejected()
    {
        var root = NewMemory();
        root.CreateDirectory("a/b");

        Assert.Throws<FileHubException>(() => root.TryOpenDirectory("a/../b", out _));
    }

    [Fact]
    public void AbsolutePath_StillRejected()
    {
        var root = NewMemory();

        Assert.Throws<FileHubException>(() => root.FileExists("/abs/file.txt"));
    }
}
