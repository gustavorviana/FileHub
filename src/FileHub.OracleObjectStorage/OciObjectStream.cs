using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Stream backed by an OCI Object Storage object. Supports chunked reads
    /// (10 MB per range request). Writes are buffered locally and committed
    /// to the backing object on <see cref="Flush"/> / <see cref="FlushAsync"/>;
    /// disposing a dirty stream also triggers a flush.
    /// Sync methods delegate to their async counterparts via
    /// <c>GetAwaiter().GetResult()</c> — the driver paths all call
    /// <c>ConfigureAwait(false)</c>, so blocking here is deadlock-free.
    /// </summary>
    internal sealed class OciObjectStream : Stream
    {
        internal const int BufferSize = 10 * 1024 * 1024;

        private readonly OracleObjectStorageFile _file;
        private readonly MemoryStream _writeBuffer;
        private readonly bool _isWrite;
        private long _position;
        private bool _hasUnflushedWrites;
        private bool _disposed;

        public event EventHandler Disposed;

        public OciObjectStream(OracleObjectStorageFile file, bool isWrite)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _isWrite = isWrite;
            _writeBuffer = isWrite ? new MemoryStream() : null;
            CanRead = !isWrite;
            CanWrite = isWrite;
        }

        public override bool CanRead { get; }
        public override bool CanSeek => CanRead;
        public override bool CanWrite { get; }

        public override long Length => _isWrite ? _writeBuffer.Length : _file.LengthInternal;

        public override long Position
        {
            get => _isWrite ? _writeBuffer.Position : _position;
            set
            {
                if (_isWrite) { _writeBuffer.Position = value; return; }
                Seek(value, SeekOrigin.Begin);
            }
        }

        public override void Flush() => SyncBridge.Run(ct => FlushAsync(ct));

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!CanWrite || !_hasUnflushedWrites) return;
            await UploadBufferAsync(cancellationToken).ConfigureAwait(false);
            _hasUnflushedWrites = false;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => SyncBridge.Run(ct => ReadAsync(buffer, offset, count, ct));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!CanRead) throw new NotSupportedException();
            ValidateReadWriteArgs(buffer, offset, count);
            if (count == 0 || _position >= Length) return 0;

            int bytesRead = 0;
            var client = _file.SessionInternal.Client;

            while (bytesRead < count && _position < Length && !_disposed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkLen = (int)Math.Min(
                    Math.Min(BufferSize, Length - _position),
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
            if (!CanSeek) throw new NotSupportedException();

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

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (!CanWrite) throw new NotSupportedException();
            ValidateReadWriteArgs(buffer, offset, count);

            _writeBuffer.Write(buffer, offset, count);
            _file.LengthInternal = _writeBuffer.Length;
            _hasUnflushedWrites = true;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!CanWrite) throw new NotSupportedException();
            ValidateReadWriteArgs(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();

            _writeBuffer.Write(buffer, offset, count);
            _file.LengthInternal = _writeBuffer.Length;
            _hasUnflushedWrites = true;
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            if (disposing)
            {
                try
                {
                    Flush();
                }
                finally
                {
                    _writeBuffer?.Dispose();
                    _disposed = true;
                    Disposed?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                // Finalizer path: no async I/O possible, but notify the parent
                // file so its "a stream is already open" latch clears. Unflushed
                // writes are lost — callers must Dispose explicitly to flush.
                _disposed = true;
                Disposed?.Invoke(this, EventArgs.Empty);
            }

            base.Dispose(disposing);
        }

#if NET8_0_OR_GREATER
        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                await base.DisposeAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeBuffer?.Dispose();
                _disposed = true;
                Disposed?.Invoke(this, EventArgs.Empty);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif

        private async Task UploadBufferAsync(CancellationToken cancellationToken)
        {
            _writeBuffer.Seek(0, SeekOrigin.Begin);
            var client = _file.SessionInternal.Client;
            var timestamp = DateTime.UtcNow.ToString("O");

            // Build the metadata payload without mutating the file's in-memory
            // tags — if PutObjectAsync fails, the parent file must stay in its
            // pre-upload state. OnWriteCommitted (below) is what promotes the
            // timestamp into TagsInternal after the object is durable.
            _file.EnsureTags();
            var meta = new System.Collections.Generic.Dictionary<string, string>(_file.TagsInternal)
            {
                [OracleObjectStorageFile.ChangedAtTag] = timestamp
            };

            await client.PutObjectAsync(
                _file.ObjectName,
                _writeBuffer,
                _writeBuffer.Length,
                contentType: null,
                meta,
                cancellationToken).ConfigureAwait(false);

            _file.OnWriteCommitted(_writeBuffer.Length, timestamp);
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

        private static void ValidateReadWriteArgs(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > buffer.Length)
                throw new ArgumentException("offset + count exceeds buffer length.", nameof(buffer));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OciObjectStream));
        }
    }
}
