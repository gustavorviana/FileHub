using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// Write stream over an S3 object. Bytes are buffered locally up to the
    /// S3 minimum part size (5 MiB); beyond that the stream transparently
    /// spills into a multipart upload and keeps memory usage constant, so
    /// any payload size is safe through every write path. Small payloads
    /// commit as a single <c>PutObject</c> on <see cref="Flush"/> /
    /// <see cref="FlushAsync"/> or dispose; spilled payloads commit on
    /// dispose (S3 cannot append, so a mid-stream flush cannot materialize
    /// a partial object). <see cref="WriteStreamPreference"/> overrides the
    /// strategy: <c>Multipart</c> spills on the first written byte,
    /// <c>Single</c> never spills.
    /// <para>
    /// Dispose contract: like <see cref="FileStream"/>, disposing a dirty
    /// stream commits the write, and a failed commit propagates out of
    /// <c>Dispose</c> — swallowing it would silently lose data. Internal
    /// state is cleaned up regardless, so the parent file is never left
    /// locked. Callers that need to separate commit errors from disposal
    /// should call <see cref="Flush"/> / <see cref="FlushAsync"/> first.
    /// </para>
    /// </summary>
    internal sealed class S3ObjectStream : S3FileStreamBase
    {
        private readonly AmazonS3File _file;
        private readonly S3WriteOptions _options;
        private readonly WriteStreamPreference _preference;
        private readonly MemoryStream _writeBuffer = new();
        private bool _hasUnflushedWrites;
        private bool _disposed;
        // Non-null once writes crossed the 5 MiB spill threshold: from then
        // on bytes stream through a multipart upload instead of accumulating
        // in memory. _spilledTotal tracks the running total (the multipart
        // stream owns the actual part buffers).
        private Stream _multipart;
        private long _spilledTotal;

        public S3ObjectStream(AmazonS3File file, S3WriteOptions options = null, WriteStreamPreference preference = WriteStreamPreference.Auto)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _options = options;
            _preference = preference;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => _multipart != null ? _spilledTotal : _writeBuffer.Length;

        public override long Position
        {
            get => _multipart != null ? _spilledTotal : _writeBuffer.Position;
            set
            {
                if (_multipart != null)
                    throw new NotSupportedException("Seeking is not supported after the stream spilled to multipart.");
                _writeBuffer.Position = value;
            }
        }

        public override void Flush() => SyncBridge.Run(ct => FlushAsync(ct));

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_hasUnflushedWrites) return;
            // Spilled: parts upload as their buffers roll over; the object can
            // only materialize at CompleteMultipartUpload (dispose) — S3 has
            // no append, so a mid-stream flush has nothing more to commit.
            if (_multipart != null) return;
            await UploadBufferAsync(cancellationToken).ConfigureAwait(false);
            _hasUnflushedWrites = false;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => SyncBridge.Run(ct => WriteAsync(buffer, offset, count, ct));

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateWriteArgs(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();

            // Multipart preference: spill on the first written byte, skipping
            // the buffering phase entirely. Single: never spill — the caller
            // opted into buffering the whole payload for one PutObject. Auto:
            // spill once the payload outgrows the part-size threshold.
            if (_multipart == null
                && _preference != WriteStreamPreference.Single
                && (_preference == WriteStreamPreference.Multipart
                    || _writeBuffer.Length + count > AmazonS3File.S3MinimumPartSize))
                await SpillToMultipartAsync(cancellationToken).ConfigureAwait(false);

            if (_multipart != null)
            {
                await _multipart.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                _spilledTotal += count;
                _file.LengthInternal = _spilledTotal;
            }
            else
            {
                _writeBuffer.Write(buffer, offset, count);
                _file.LengthInternal = _writeBuffer.Length;
            }
            _hasUnflushedWrites = true;
        }

        /// <summary>
        /// Switches from the in-memory buffer to a multipart upload: replays
        /// the bytes buffered so far into the multipart stream and truncates
        /// the local buffer. A failure inside the multipart stream aborts the
        /// upload server-side (its own write path guarantees that).
        /// </summary>
        private async Task SpillToMultipartAsync(CancellationToken cancellationToken)
        {
            var multipart = await _file.GetMultipartWriteStreamAsync(_options, cancellationToken).ConfigureAwait(false);
            try
            {
                _writeBuffer.Seek(0, SeekOrigin.Begin);
                await _writeBuffer.CopyToAsync(multipart, 81920, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The multipart stream aborted itself on the failed write;
                // disposing it after an abort is a safe no-op.
                multipart.Dispose();
                throw;
            }
            _spilledTotal = _writeBuffer.Length;
            _multipart = multipart;
            _writeBuffer.SetLength(0);
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
                    // Spilled: disposing the multipart stream uploads the
                    // trailing part and commits (or aborts on failure).
                    if (_multipart != null) _multipart.Dispose();
                    else Flush();
                }
                finally
                {
                    // Mark disposed and notify the parent file BEFORE touching
                    // _writeBuffer. If the buffer's Dispose ever throws, we
                    // still want _lastOpenStream on the parent to be cleared
                    // — otherwise the file is permanently locked from
                    // opening another stream.
                    _disposed = true;
                    RaiseDisposed();
                    try { _writeBuffer.Dispose(); } catch { /* swallow — best effort */ }
                }
            }
            else
            {
                // Finalizer path: we can't do async I/O, but we must still
                // notify the parent file so its "a stream is already open"
                // latch clears. Any unflushed writes in _writeBuffer are lost
                // (documented: callers must Dispose explicitly to flush).
                _disposed = true;
                RaiseDisposed();
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
                if (_multipart != null) await _multipart.DisposeAsync().ConfigureAwait(false);
                else await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _disposed = true;
                RaiseDisposed();
                try { _writeBuffer.Dispose(); } catch { /* swallow — best effort */ }
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif

        private async Task UploadBufferAsync(CancellationToken cancellationToken)
        {
            _writeBuffer.Seek(0, SeekOrigin.Begin);
            var client = _file.SessionInternal.Client;

            await client.PutObjectAsync(
                _file.ObjectKey,
                _writeBuffer,
                _writeBuffer.Length,
                _options,
                cancellationToken).ConfigureAwait(false);

            _file.OnWriteCommitted(_writeBuffer.Length, _options);
        }

        private static void ValidateWriteArgs(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > buffer.Length)
                throw new ArgumentException("offset + count exceeds buffer length.", nameof(buffer));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(S3ObjectStream));
        }
    }
}
