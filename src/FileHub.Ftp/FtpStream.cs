using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Ftp
{
    /// <summary>
    /// Thin wrapper around a FluentFTP read/write stream. FTP data channels
    /// are sequential, so seeking is not supported. On dispose the wrapper
    /// closes the underlying data channel and (for writes) marks the parent
    /// <see cref="FtpFile"/> for refresh so subsequent length/timestamp reads
    /// query the server again.
    /// </summary>
    internal sealed class FtpStream : Stream
    {
        private readonly Stream _inner;
        private readonly FtpFile _file;
        private readonly bool _isWrite;
        private long _bytesWritten;
        private bool _disposed;

        public event EventHandler Disposed;

        public FtpStream(Stream inner, FtpFile file, bool isWrite)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _isWrite = isWrite;
        }

        public override bool CanRead => !_isWrite && !_disposed && _inner.CanRead;
        public override bool CanWrite => _isWrite && !_disposed && _inner.CanWrite;
        public override bool CanSeek => false;

        public override long Length =>
            _isWrite ? throw new NotSupportedException("Length is not supported on an FTP write stream.")
                     : _file.LengthInternal;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException("Seeking is not supported on FTP streams.");
        }

        public override void Flush()
        {
            ThrowIfDisposed();
            _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (!CanRead) throw new NotSupportedException();
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!CanRead) throw new NotSupportedException();
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (!CanWrite) throw new NotSupportedException();
            _inner.Write(buffer, offset, count);
            _bytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!CanWrite) throw new NotSupportedException();
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            _bytesWritten += count;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("Seeking is not supported on FTP streams.");

        public override void SetLength(long value) =>
            throw new NotSupportedException("SetLength is not supported on FTP streams.");

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
                    _inner.Dispose();
                }
                finally
                {
                    if (_isWrite) _file.OnWriteCompleted(_bytesWritten);
                    _disposed = true;
                    Disposed?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                _disposed = true;
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
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_isWrite) _file.OnWriteCompleted(_bytesWritten);
                _disposed = true;
                Disposed?.Invoke(this, EventArgs.Empty);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FtpStream));
        }
    }
}
