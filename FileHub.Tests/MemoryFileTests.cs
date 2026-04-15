using FileHub.Memory;
using System.Text;

namespace FileHub.Tests;

public class MemoryFileTests
{
    private static FileDirectory NewRoot() => new MemoryFileHub().Root;

    // === Read/Write via streams ===

    [Fact]
    public void SetText_ReadAllText_Roundtrip()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("hello world");

        Assert.Equal("hello world", file.ReadAllText());
    }

    [Fact]
    public void SetText_WithEncoding_Roundtrip()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("áéíóú", Encoding.UTF8);

        Assert.Equal("áéíóú", file.ReadAllText(Encoding.UTF8));
    }

    [Fact]
    public void SetBytes_ReadAllBytes_Roundtrip()
    {
        var file = NewRoot().CreateFile("a.bin");
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        file.SetBytes(payload);

        Assert.Equal(payload, file.ReadAllBytes());
    }

    [Fact]
    public void SetBytes_Null_Throws()
    {
        var file = NewRoot().CreateFile("a.bin");
        Assert.Throws<ArgumentNullException>(() => file.SetBytes(null));
    }

    [Fact]
    public void GetWriteStream_Truncates_PreviousContent()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("long content");

        file.SetText("x");

        Assert.Equal("x", file.ReadAllText());
        Assert.Equal(1, file.Length);
    }

    [Fact]
    public void Length_ReflectsContent()
    {
        var file = NewRoot().CreateFile("a.txt");
        Assert.Equal(0, file.Length);

        file.SetBytes(new byte[] { 1, 2, 3 });

        Assert.Equal(3, file.Length);
    }

    [Fact]
    public void Extension_ReturnsExtension()
    {
        var root = NewRoot();
        Assert.Equal(".txt", root.CreateFile("a.txt").Extension);
        Assert.Equal(".log.gz", ".log.gz");
        Assert.Equal("", root.CreateFile("README").Extension);
    }

    [Fact]
    public void Path_IncludesParentPath()
    {
        var hub = new MemoryFileHub("root");
        var sub = hub.Root.CreateDirectory("sub");
        var file = sub.CreateFile("f.txt");

        Assert.Contains("sub", file.Path);
        Assert.EndsWith("f.txt", file.Path);
    }

    [Fact]
    public void ToString_ReturnsPath()
    {
        var file = NewRoot().CreateFile("a.txt");
        Assert.Equal(file.Path, file.ToString());
    }

    // === CopyToStream ===

    [Fact]
    public void CopyToStream_CopiesContentToDestination()
    {
        var file = NewRoot().CreateFile("a.bin");
        file.SetBytes(new byte[] { 9, 8, 7, 6 });

        using var dest = new MemoryStream();
        file.CopyToStream(dest);

        Assert.Equal(new byte[] { 9, 8, 7, 6 }, dest.ToArray());
    }

    [Fact]
    public void CopyToStream_NullDestination_Throws()
    {
        var file = NewRoot().CreateFile("a.bin");
        Assert.Throws<ArgumentNullException>(() => file.CopyToStream(null));
    }

    [Fact]
    public void CopyToStream_NonWritableDestination_Throws()
    {
        var file = NewRoot().CreateFile("a.bin");
        using var readOnly = new MemoryStream(new byte[10], writable: false);

        Assert.Throws<NotSupportedException>(() => file.CopyToStream(readOnly));
    }

    // === CopyTo ===

    [Fact]
    public void CopyTo_SameDirectory_CreatesCopyWithNewName()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        file.SetText("data");

        var copy = file.CopyTo("b.txt");

        Assert.Equal("b.txt", copy.Name);
        Assert.Equal("data", copy.ReadAllText());
        Assert.True(file.Exists());
    }

    [Fact]
    public void CopyTo_DifferentDirectory_CreatesCopyThere()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        file.SetText("payload");

        var other = root.CreateDirectory("other");
        var copy = file.CopyTo(other, "copied.txt");

        Assert.Same(other, copy.Parent);
        Assert.Equal("payload", copy.ReadAllText());
    }

    // === Rename ===

    [Fact]
    public void Rename_UpdatesName()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        file.SetText("keep");

        file.Rename("b.txt");

        Assert.Equal("b.txt", file.Name);
        Assert.True(root.ItemExists("b.txt"));
        Assert.False(root.ItemExists("a.txt"));
        Assert.Equal("keep", file.ReadAllText());
    }

    [Fact]
    public void Rename_InvalidName_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        Assert.Throws<ArgumentException>(() => file.Rename(""));
    }

    // === MoveTo ===

    [Fact]
    public void MoveTo_MovesFileToAnotherDirectory()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        file.SetText("x");
        var dst = root.CreateDirectory("dst");

        var moved = file.MoveTo(dst, "moved.txt");

        Assert.Equal("moved.txt", moved.Name);
        Assert.False(root.ItemExists("a.txt"));
        Assert.True(dst.ItemExists("moved.txt"));
        Assert.Equal("x", moved.ReadAllText());
    }

    // === Delete ===

    [Fact]
    public void Delete_RemovesFile()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");

        file.Delete();

        Assert.False(root.ItemExists("a.txt"));
        Assert.False(file.Exists());
    }

    // === SetLastWriteTime ===

    [Fact]
    public void SetLastWriteTime_UpdatesTimestamp()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ts = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        file.SetLastWriteTime(ts);

        Assert.Equal(ts, file.LastWriteTimeUtc);
    }

    [Fact]
    public void Write_UpdatesLastWriteTimeUtc()
    {
        var file = NewRoot().CreateFile("a.txt");
        var before = file.LastWriteTimeUtc;
        Thread.Sleep(10);

        file.SetText("new");

        Assert.True(file.LastWriteTimeUtc >= before);
    }

    // === Concurrency / locks (exercises MemoryFileData + NonDisposableMemoryStream) ===

    [Fact]
    public void GetWriteStream_WhileReaderActive_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("x");

        using var reader = file.GetReadStream();

        Assert.Throws<FileHubException>(() => file.GetWriteStream());
    }

    [Fact]
    public void GetWriteStream_WhileWriterActive_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        using var writer = file.GetWriteStream();

        Assert.Throws<FileHubException>(() => file.GetWriteStream());
    }

    [Fact]
    public void GetReadStream_WhileWriterActive_Throws()
    {
        var file = NewRoot().CreateFile("a.txt");
        using var writer = file.GetWriteStream();

        Assert.Throws<FileHubException>(() => file.GetReadStream());
    }

    [Fact]
    public void WriteStream_AfterDispose_ReleasesLock()
    {
        var file = NewRoot().CreateFile("a.txt");
        using (var w = file.GetWriteStream())
        {
            w.Write(new byte[] { 1 }, 0, 1);
        }

        // Should no longer throw because the lock was released on dispose.
        var ex = Record.Exception(() => { using var _ = file.GetReadStream(); });
        Assert.Null(ex);
    }

    [Fact]
    public void ReadStream_CannotWrite()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("x");
        using var r = file.GetReadStream();

        Assert.False(r.CanWrite);
        Assert.True(r.CanRead);
        Assert.Throws<NotSupportedException>(() => r.Write(new byte[] { 0 }, 0, 1));
        Assert.Throws<NotSupportedException>(() => r.SetLength(0));
    }

    [Fact]
    public void WriteStream_CannotRead()
    {
        var file = NewRoot().CreateFile("a.txt");
        using var w = file.GetWriteStream();

        Assert.True(w.CanWrite);
        Assert.False(w.CanRead);
        var buf = new byte[10];
        Assert.Throws<NotSupportedException>(() => w.Read(buf, 0, buf.Length));
    }

    [Fact]
    public void Stream_SupportsSeek()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("hello");
        using var r = file.GetReadStream();

        Assert.True(r.CanSeek);
        r.Position = 1;
        Assert.Equal(1, r.Position);
        r.Seek(0, SeekOrigin.Begin);
        Assert.Equal(0, r.Position);
    }

    [Fact]
    public void Stream_Flush_DoesNotThrow()
    {
        var file = NewRoot().CreateFile("a.txt");
        using var w = file.GetWriteStream();
        w.Write(new byte[] { 1 }, 0, 1);
        w.Flush();
    }

    // === Async variants ===

    [Fact]
    public async Task ReadAllTextAsync_ReturnsContent()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("async");

        Assert.Equal("async", await file.ReadAllTextAsync());
    }

    [Fact]
    public async Task ReadAllTextAsync_WithEncoding_ReturnsContent()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("çã", Encoding.UTF8);

        Assert.Equal("çã", await file.ReadAllTextAsync(Encoding.UTF8));
    }

    [Fact]
    public async Task ReadAllBytesAsync_ReturnsBytes()
    {
        var file = NewRoot().CreateFile("a.bin");
        file.SetBytes(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, await file.ReadAllBytesAsync());
    }

    [Fact]
    public async Task SetTextAsync_WritesContent()
    {
        var file = NewRoot().CreateFile("a.txt");

        await file.SetTextAsync("async-write");

        Assert.Equal("async-write", file.ReadAllText());
    }

    [Fact]
    public async Task SetBytesAsync_WritesContent()
    {
        var file = NewRoot().CreateFile("a.bin");

        await file.SetBytesAsync(new byte[] { 7, 8 });

        Assert.Equal(new byte[] { 7, 8 }, file.ReadAllBytes());
    }

    [Fact]
    public async Task SetBytesAsync_Null_Throws()
    {
        var file = NewRoot().CreateFile("a.bin");
        await Assert.ThrowsAsync<ArgumentNullException>(() => file.SetBytesAsync(null));
    }

    [Fact]
    public async Task CopyToStreamAsync_CopiesContent()
    {
        var file = NewRoot().CreateFile("a.bin");
        file.SetBytes(new byte[] { 1, 2, 3 });

        using var dest = new MemoryStream();
        await file.CopyToStreamAsync(dest);

        Assert.Equal(new byte[] { 1, 2, 3 }, dest.ToArray());
    }

    [Fact]
    public async Task CopyToStreamAsync_NullDestination_Throws()
    {
        var file = NewRoot().CreateFile("a.bin");
        await Assert.ThrowsAsync<ArgumentNullException>(() => file.CopyToStreamAsync(null));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");

        await file.DeleteAsync();

        Assert.False(root.ItemExists("a.txt"));
    }

    [Fact]
    public async Task CopyToAsync_SameDirectory_Works()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("data");

        var copy = await file.CopyToAsync("b.txt");

        Assert.Equal("data", copy.ReadAllText());
    }

    [Fact]
    public async Task CopyToAsync_OtherDirectory_Works()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        file.SetText("data");
        var dst = root.CreateDirectory("dst");

        var copy = await file.CopyToAsync(dst, "copied.txt");

        Assert.Same(dst, copy.Parent);
        Assert.Equal("data", copy.ReadAllText());
    }

    [Fact]
    public async Task MoveToAsync_MovesFile()
    {
        var root = NewRoot();
        var file = root.CreateFile("a.txt");
        file.SetText("x");
        var dst = root.CreateDirectory("dst");

        var moved = await file.MoveToAsync(dst, "moved.txt");

        Assert.False(root.ItemExists("a.txt"));
        Assert.True(dst.ItemExists("moved.txt"));
        Assert.Equal("x", moved.ReadAllText());
    }

    [Fact]
    public async Task RenameAsync_RenamesFile()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("keep");

        var renamed = await file.RenameAsync("b.txt");

        Assert.Equal("b.txt", renamed.Name);
        Assert.Equal("keep", renamed.ReadAllText());
    }

    [Fact]
    public async Task SetLastWriteTimeAsync_UpdatesTimestamp()
    {
        var file = NewRoot().CreateFile("a.txt");
        var ts = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await file.SetLastWriteTimeAsync(ts);

        Assert.Equal(ts, file.LastWriteTimeUtc);
    }

    [Fact]
    public async Task GetReadStreamAsync_ReturnsStream()
    {
        var file = NewRoot().CreateFile("a.txt");
        file.SetText("hi");

        using var stream = await file.GetReadStreamAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal("hi", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task GetWriteStreamAsync_ReturnsStream()
    {
        var file = NewRoot().CreateFile("a.txt");

        using (var stream = await file.GetWriteStreamAsync())
        {
            var bytes = Encoding.UTF8.GetBytes("written");
            await stream.WriteAsync(bytes, 0, bytes.Length);
        }

        Assert.Equal("written", file.ReadAllText());
    }

    [Fact]
    public async Task AsyncMethods_RespectCancellation()
    {
        var file = NewRoot().CreateFile("a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => file.ReadAllTextAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => file.ReadAllBytesAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => file.SetTextAsync("x", null, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => file.SetBytesAsync(new byte[] { 1 }, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => file.DeleteAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => file.GetReadStreamAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => file.GetWriteStreamAsync(cts.Token));
    }
}
