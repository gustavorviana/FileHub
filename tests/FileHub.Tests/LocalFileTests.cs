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
    public void Rename_ToExistingName_ThrowsAndKeepsBoth()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.txt");
        file.SetText("a");
        root.CreateFile("b.txt").SetText("b");

        Assert.Throws<FileAlreadyExistsException>(() => file.Rename("b.txt"));
        // Never overwrites — both survive untouched.
        Assert.Equal("a", root.OpenFile("a.txt").ReadAllText());
        Assert.Equal("b", root.OpenFile("b.txt").ReadAllText());
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

    // === Public constructor ===

    [Fact]
    public void PublicConstructor_CreatesReferenceWithoutTouchingDisk()
    {
        using var temp = new TempDirectory();
        var root = (LocalDirectory)new LocalFileHub(temp.Path).Root;

        var file = new LocalFile(root, "a.txt");

        Assert.Equal("a.txt", file.Name);
        Assert.Same(root, file.Parent);
        Assert.False(file.Exists());
        Assert.False(File.Exists(Path.Combine(temp.Path, "a.txt")));
    }

    [Fact]
    public void PublicConstructor_CanWriteAndReadBack()
    {
        using var temp = new TempDirectory();
        var root = (LocalDirectory)new LocalFileHub(temp.Path).Root;

        var file = new LocalFile(root, "hello.txt");
        file.SetText("hi");

        Assert.True(file.Exists());
        Assert.Equal("hi", file.ReadAllText());
    }

    [Fact]
    public void PublicConstructor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalFile(null!, "a.txt"));
    }

    [Fact]
    public void PublicConstructor_InvalidName_Throws()
    {
        using var temp = new TempDirectory();
        var root = (LocalDirectory)new LocalFileHub(temp.Path).Root;

        Assert.Throws<ArgumentException>(() => new LocalFile(root, ""));
        Assert.Throws<ArgumentException>(() => new LocalFile(root, ".."));
        Assert.Throws<ArgumentException>(() => new LocalFile(root, "a/b.txt"));
    }

    // === Progress reporting ===

    // Larger than the 80 KB copy-loop buffer so a streamed copy yields more
    // than one progress report.
    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++) data[i] = (byte)i;
        return data;
    }

    // Synchronous collector — the driver calls Report() inline on the copy
    // loop, so reads are deterministic (unlike Progress<T>, which posts async).
    private sealed class ProgressCollector : IProgress<TransferStatus>
    {
        public List<TransferStatus> Reports { get; } = new();
        public void Report(TransferStatus value) => Reports.Add(value);
    }

    [Fact]
    public void CopyTo_ReportsGranularProgress()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.bin");
        var payload = Payload(200_000);
        file.SetBytes(payload);
        var progress = new ProgressCollector();

        var copy = file.CopyTo(root, "b.bin", progress);

        Assert.Equal(payload, copy.ReadAllBytes());
        Assert.True(progress.Reports.Count > 1, $"expected granular progress, got {progress.Reports.Count} report(s)");
        Assert.Equal(payload.Length, progress.Reports[^1].BytesTransferred);
        Assert.Equal(payload.Length, progress.Reports[^1].TotalBytes);
    }

    [Fact]
    public void MoveTo_ReportsGranularProgress()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var file = root.CreateFile("a.bin");
        var payload = Payload(200_000);
        file.SetBytes(payload);
        var dst = root.CreateDirectory("dst");
        var progress = new ProgressCollector();

        var moved = file.MoveTo(dst, "moved.bin", progress);

        Assert.Equal(payload, moved.ReadAllBytes());
        Assert.False(File.Exists(Path.Combine(temp.Path, "a.bin")));
        Assert.True(progress.Reports.Count > 1, $"expected granular progress, got {progress.Reports.Count} report(s)");
        Assert.Equal(payload.Length, progress.Reports[^1].BytesTransferred);
    }

    // === Overwrite ===

    [Fact]
    public void CopyTo_DefaultOverwritesExisting()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateFile("a.txt");
        src.SetText("new");
        root.CreateFile("b.txt").SetText("old");

        var copy = src.CopyTo(root, "b.txt");

        Assert.Equal("new", copy.ReadAllText());
    }

    [Fact]
    public void CopyTo_OverwriteFalse_ThrowsWhenDestinationExists()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateFile("a.txt");
        src.SetText("new");
        root.CreateFile("b.txt").SetText("old");

        var ex = Assert.Throws<FileAlreadyExistsException>(() => src.CopyTo(root, "b.txt", progress: null, overwrite: false));
        // Source untouched, destination preserved.
        Assert.Equal("old", root.OpenFile("b.txt").ReadAllText());
        Assert.Contains("b.txt", ex.DestinationPath);
    }

    [Fact]
    public void CopyTo_OverwriteFalse_SucceedsWhenDestinationAbsent()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateFile("a.txt");
        src.SetText("data");

        var copy = src.CopyTo(root, "b.txt", progress: null, overwrite: false);

        Assert.Equal("data", copy.ReadAllText());
    }

    [Fact]
    public void MoveTo_OverwriteFalse_ThrowsAndKeepsSource()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateFile("a.txt");
        src.SetText("new");
        var dst = root.CreateDirectory("dst");
        dst.CreateFile("a.txt").SetText("old");

        Assert.Throws<FileAlreadyExistsException>(() => src.MoveTo(dst, "a.txt", progress: null, overwrite: false));
        // Move aborted before delete — source still there.
        Assert.True(File.Exists(Path.Combine(temp.Path, "a.txt")));
        Assert.Equal("old", dst.OpenFile("a.txt").ReadAllText());
    }

    [Fact]
    public async Task CopyToAsync_OverwriteFalse_ThrowsWhenDestinationExists()
    {
        using var temp = new TempDirectory();
        var root = NewRoot(temp);
        var src = root.CreateFile("a.txt");
        src.SetText("new");
        root.CreateFile("b.txt").SetText("old");

        await Assert.ThrowsAsync<FileAlreadyExistsException>(
            () => src.CopyToAsync(root, "b.txt", progress: null, overwrite: false));
        Assert.Equal("old", root.OpenFile("b.txt").ReadAllText());
    }
}
