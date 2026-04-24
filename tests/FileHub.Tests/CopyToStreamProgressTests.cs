using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FileHub.Memory;

namespace FileHub.Tests;

public class CopyToStreamProgressTests
{
    private const int ChunkSize = 81920;

    private static MemoryFile CreateFile(byte[] payload)
    {
        var hub = new MemoryFileHub();
        var file = (MemoryFile)hub.Root.CreateFile("payload.bin");
        file.SetBytes(payload);
        return file;
    }

    private static byte[] MakePayload(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }

    [Fact]
    public async Task CopyToStreamAsync_WithProgress_ReportsBytesInChunksMonotonic()
    {
        // 250 KB → at least 3 chunks of 80 KB plus a tail.
        var payload = MakePayload(ChunkSize * 3 + 1234);
        var file = CreateFile(payload);
        var reports = new List<TransferStatus>();
        var progress = new Progress<TransferStatus>(reports.Add);

        using var dest = new MemoryStream();
        await file.CopyToStreamAsync(dest, progress);

        // Progress<T> marshals callbacks via the sync context; give them a moment to drain.
        for (int i = 0; i < 20 && reports.Count < 4; i++)
            await Task.Delay(10);

        Assert.True(reports.Count >= 4, $"expected at least 4 reports, got {reports.Count}");

        long previous = 0;
        foreach (var status in reports)
        {
            Assert.True(status.BytesTransferred > previous, "BytesTransferred must be strictly monotonic");
            Assert.Equal(payload.Length, status.TotalBytes);
            previous = status.BytesTransferred;
        }

        Assert.Equal(payload.Length, reports[^1].BytesTransferred);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public async Task CopyToStreamAsync_WithNullProgress_UsesFastPath()
    {
        var payload = MakePayload(ChunkSize * 2 + 7);
        var file = CreateFile(payload);

        using var dest = new MemoryStream();
        await file.CopyToStreamAsync(dest);

        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public void CopyToStream_WithProgress_ReportsBytesInChunksMonotonic()
    {
        var payload = MakePayload(ChunkSize * 3 + 500);
        var file = CreateFile(payload);
        var reports = new List<TransferStatus>();
        var progress = new SyncProgress<TransferStatus>(reports.Add);

        using var dest = new MemoryStream();
        file.CopyToStream(dest, progress);

        Assert.True(reports.Count >= 4);

        long previous = 0;
        foreach (var status in reports)
        {
            Assert.True(status.BytesTransferred > previous);
            Assert.Equal(payload.Length, status.TotalBytes);
            previous = status.BytesTransferred;
        }

        Assert.Equal(payload.Length, reports[^1].BytesTransferred);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public async Task CopyToStreamAsync_WithProgress_UnknownLength_ReportsMinusOneTotal()
    {
        var payload = MakePayload(ChunkSize + 100);
        var file = new UnknownLengthFile(payload);
        var reports = new List<TransferStatus>();
        var progress = new SyncProgress<TransferStatus>(reports.Add);

        using var dest = new MemoryStream();
        await file.CopyToStreamAsync(dest, progress);

        Assert.NotEmpty(reports);
        long previous = 0;
        foreach (var status in reports)
        {
            Assert.Equal(-1, status.TotalBytes);
            Assert.True(status.BytesTransferred > previous);
            previous = status.BytesTransferred;
        }
        Assert.Equal(payload.Length, reports[^1].BytesTransferred);
        Assert.Equal(payload, dest.ToArray());
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private sealed class UnknownLengthFile : FileEntry
    {
        private readonly byte[] _payload;
        public UnknownLengthFile(byte[] payload) : base("unknown.bin") => _payload = payload;
        public override string Path => "/" + Name;
        public override long Length => -1;
        public override DateTime CreationTimeUtc => default;
        public override DateTime LastWriteTimeUtc => default;
        public override FileDirectory Parent => null!;
        public override bool Exists() => true;
        public override Stream GetReadStream() => new MemoryStream(_payload, writable: false);
        public override Stream GetWriteStream() => throw new NotSupportedException();
        public override void Delete() => throw new NotSupportedException();
        public override FileEntry Rename(string newName) => throw new NotSupportedException();
        public override FileEntry MoveTo(FileDirectory directory, string name) => throw new NotSupportedException();
    }
}
