using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Read-only stream over an OCI Object Storage object. Reads are chunked
    /// into ranged <c>GetObject</c> requests (10 MB per range) so any object
    /// size streams without loading fully into memory. Seekable; length comes
    /// from the parent file's cached snapshot.
    /// Sync methods delegate to their async counterparts via
    /// <c>SyncBridge.Run</c>, which queues the work to the thread pool —
    /// deadlock-free on any host.
    /// </summary>
    internal sealed class OciReadStream : OciFileStreamBase
    {
        internal const int ReadChunkSize = 10 * 1024 * 1024;

        private readonly OracleObjectStorageFile _file;
        private long _position;
        private bool _disposed;

        public OciReadStream(OracleObjectStorageFile file)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Length => _file.LengthInternal;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() { /* read-only: nothing to flush */ }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => SyncBridge.Run(ct => ReadAsync(buffer, offset, count, ct));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateReadArgs(buffer, offset, count);
            if (count == 0 || _position >= Length) return 0;

            int bytesRead = 0;
            var client = _file.SessionInternal.Client;

            while (bytesRead < count && _position < Length && !_disposed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkLen = (int)Math.Min(
                    Math.Min(ReadChunkSize, Length - _position),
                    count - bytesRead);
                long endByte = _position + chunkLen - 1;

                var getResult = await client.GetObjectAsync(_file.ObjectName, _position, endByte, cancellationToken).ConfigureAwait(false);

                using (var source = getResult.InputStream)
                {
                    int inChunk = await FillFromSourceAsync(source, buffer, offset + bytesRead, chunkLen, cancellationToken).ConfigureAwait(false);
                    if (inChunk == 0) break;
                    _position += inChunk;
                    bytesRead += inChunk;
                }
            }

            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();

            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
            };

            if (newPosition < 0)
                throw new IOException("Seek resulted in a negative position.");
            if (newPosition > Length)
                throw new IOException("Seek past end of stream.");

            _position = newPosition;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                // No pending state on a read stream — just clear the parent
                // file's "a stream is already open" latch.
                _disposed = true;
                RaiseDisposed();
            }
            base.Dispose(disposing);
        }

        private static async Task<int> FillFromSourceAsync(Stream source, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < count)
            {
                int got = await source.ReadAsync(buffer, offset + total, count - total, cancellationToken).ConfigureAwait(false);
                if (got == 0) break;
                total += got;
            }
            return total;
        }

        private static void ValidateReadArgs(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > buffer.Length)
                throw new ArgumentException("offset + count exceeds buffer length.", nameof(buffer));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OciReadStream));
        }
    }
}
