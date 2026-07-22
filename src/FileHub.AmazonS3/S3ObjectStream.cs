using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// Write stream over an S3 object. Bytes are buffered locally up to the
    /// configured multipart threshold; beyond that the stream transparently
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
        private readonly MultipartStreamOptions _multipartStreamOptions;
        private readonly WriteStreamPreference _preference;
        private readonly S3WriteOptions _options;
        private readonly AmazonS3File _file;
        private Stream _writeBuffer;

        private bool _hasUnflushedWrites;
        private bool _multipart;
        private bool _disposed;

        public S3ObjectStream(AmazonS3File file, S3WriteOptions options, MultipartStreamOptions multipartStreamOptions)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _options = options;
            _preference = options?.StreamPreference ?? WriteStreamPreference.Auto;
            _multipartStreamOptions = multipartStreamOptions;

            if (_preference != WriteStreamPreference.Multipart)
                _writeBuffer = new MemoryStream();
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => _writeBuffer.Length;

        public override long Position
        {
            get => _writeBuffer.Position;
            set
            {
                if (_multipart)
                    throw new NotSupportedException("Seeking is not supported after the stream spilled to multipart.");

                _writeBuffer.Position = value;
            }
        }

        public override void Flush() => SyncBridge.Run(FlushAsync);

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!_hasUnflushedWrites || _multipart) return;

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

            if (NeedsSpill(count))
                await SpillToMultipartAsync(cancellationToken).ConfigureAwait(false);

            if (_multipart)
            {
                await _writeBuffer.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ValidateWriteArgs(buffer, offset, count);
                _writeBuffer.Write(buffer, offset, count);
                _file.LengthInternal = _writeBuffer.Length;
                _hasUnflushedWrites = true;
            }
        }

        private bool NeedsSpill(int count)
        {
            if (_multipart || _preference == WriteStreamPreference.Single)
                return false;

            return _preference == WriteStreamPreference.Multipart ||
                _writeBuffer.Length + count > _multipartStreamOptions.Threshold;
        }

        /// <summary>
        /// Switches from the in-memory buffer to a multipart upload: replays
        /// the bytes buffered so far into the multipart stream and truncates
        /// the local buffer. A failure inside the multipart stream aborts the
        /// upload server-side (its own write path guarantees that).
        /// </summary>
        private async Task SpillToMultipartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uploadId = await _file.SessionInternal.Client.BeginMultipartUploadAsync(_file.ObjectKey, _options, cancellationToken).ConfigureAwait(false);

            _writeBuffer = new S3MultipartWriteStream(_file, uploadId, _options, (MemoryStream)_writeBuffer, _multipartStreamOptions.PartSize);
            _multipart = true;
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
                    if (!_multipart && _preference == WriteStreamPreference.Multipart)
                    {
                        SyncBridge.Run(UploadBufferAsync);
                    }
                    else
                    {
                        Flush();
                    }
                }
                finally
                {
                    _disposed = true;
                    RaiseDisposed();
                    try { _writeBuffer.Dispose(); } catch { /* swallow — best effort */ }
                }
            }
            else
            {
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
                if (_multipart)
                {
                    await _writeBuffer.DisposeAsync().ConfigureAwait(false);
                }
                else if (_preference == WriteStreamPreference.Multipart)
                {
                    await UploadBufferAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
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
            var writeBuffer = _writeBuffer ?? new MemoryStream();
            writeBuffer.Seek(0, SeekOrigin.Begin);
            var client = _file.SessionInternal.Client;

            await client.PutObjectAsync(
                _file.ObjectKey,
                writeBuffer,
                writeBuffer.Length,
                _options,
                cancellationToken).ConfigureAwait(false);

            _file.OnWriteCommitted(writeBuffer.Length, _options);
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
