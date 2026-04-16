using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OciObjectStreamTests : IClassFixture<InMemoryOciFixture>
{
    private readonly InMemoryOciFixture _fixture;
    private FileDirectory Root => _fixture.FileHub.Root;

    public OciObjectStreamTests(InMemoryOciFixture fixture) => _fixture = fixture;

    private FileDirectory Scope(string name) => Root.OpenDirectory(name, createIfNotExists: true);

    [Fact]
    public void WriteStream_CommitsOnDispose()
    {
        var scope = Scope(nameof(WriteStream_CommitsOnDispose));
        var file = scope.CreateFile("w.txt");

        using (var stream = file.GetWriteStream())
        {
            var bytes = new byte[] { 1, 2, 3 };
            stream.Write(bytes, 0, bytes.Length);
        } // Dispose triggers Flush → PutObject

        Assert.Equal(new byte[] { 1, 2, 3 }, scope.OpenFile("w.txt").ReadAllBytes());
    }

    [Fact]
    public void Flush_CommitsWithoutDispose()
    {
        var scope = Scope(nameof(Flush_CommitsWithoutDispose));
        var file = scope.CreateFile("f.txt");

        using var stream = file.GetWriteStream();
        var bytes = new byte[] { 10, 20, 30 };
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        // The driver exposes a single-open-stream guard — reopening the same file
        // for read while holding the write stream would throw. So verify via fake.
        Assert.True(_fixture.Client.TryGetBody(
            $"{nameof(Flush_CommitsWithoutDispose)}/f.txt", out var stored));
        Assert.Equal(bytes, stored);
    }

    [Fact]
    public void Flush_WithNoWrites_DoesNotUpload()
    {
        var scope = Scope(nameof(Flush_WithNoWrites_DoesNotUpload));
        var file = scope.CreateFile("untouched.txt");

        var beforeLast = _fixture.Client.Keys.Count;

        using (var stream = file.GetWriteStream())
        {
            stream.Flush(); // buffer empty → no-op
        }

        // CreateFile itself did one PutObject; no additional objects appeared.
        Assert.Equal(beforeLast, _fixture.Client.Keys.Count);
    }

    [Fact]
    public void Read_ChunkedAcross10MbBoundary_ReadsAllBytes()
    {
        var scope = Scope(nameof(Read_ChunkedAcross10MbBoundary_ReadsAllBytes));
        var payload = new byte[25 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);

        var file = scope.CreateFile("big.bin");
        file.SetBytes(payload);

        var reopened = scope.OpenFile("big.bin");
        using var ms = new MemoryStream();
        using (var src = reopened.GetReadStream())
            src.CopyTo(ms);

        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(ms.ToArray()));
    }

    [Fact]
    public void Seek_Beyond_Length_Throws()
    {
        var scope = Scope(nameof(Seek_Beyond_Length_Throws));
        var file = scope.CreateFile("seek.bin");
        file.SetBytes(new byte[] { 1, 2, 3, 4, 5 });

        var reopened = scope.OpenFile("seek.bin");
        using var stream = reopened.GetReadStream();

        Assert.Throws<IOException>(() => stream.Seek(100, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void Seek_SetsPosition()
    {
        var scope = Scope(nameof(Seek_SetsPosition));
        var file = scope.CreateFile("seek2.bin");
        file.SetBytes(new byte[] { 1, 2, 3, 4, 5 });

        var reopened = scope.OpenFile("seek2.bin");
        using var stream = reopened.GetReadStream();

        stream.Seek(2, SeekOrigin.Begin);
        Assert.Equal(2, stream.Position);

        var buf = new byte[3];
        int read = stream.Read(buf, 0, buf.Length);
        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 3, 4, 5 }, buf);
    }

    [Fact]
    public void Read_FromWriteStream_Throws()
    {
        var scope = Scope(nameof(Read_FromWriteStream_Throws));
        var file = scope.CreateFile("rw.bin");
        using var ws = file.GetWriteStream();

        var buf = new byte[4];
        Assert.Throws<NotSupportedException>(() => ws.Read(buf, 0, buf.Length));
    }

    [Fact]
    public async Task ReadAsync_Cancellation_Throws()
    {
        var scope = Scope(nameof(ReadAsync_Cancellation_Throws));
        var file = scope.CreateFile("cancel.bin");
        file.SetBytes(new byte[] { 1, 2, 3 });

        var reopened = scope.OpenFile("cancel.bin");
        using var stream = reopened.GetReadStream();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var buf = new byte[3];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(buf, 0, buf.Length, cts.Token));
    }

    [Fact]
    public void Write_AfterDispose_Throws()
    {
        var scope = Scope(nameof(Write_AfterDispose_Throws));
        var file = scope.CreateFile("d.txt");
        var stream = file.GetWriteStream();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Write(new byte[] { 1 }, 0, 1));
    }
}
