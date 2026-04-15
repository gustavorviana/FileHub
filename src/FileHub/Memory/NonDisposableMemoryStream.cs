using System;
using System.IO;

namespace FileHub.Memory
{
    /// <summary>
    /// Wraps the MemoryFileData.Stream so consumers can use it with a normal
    /// using block without disposing the underlying shared MemoryStream.
    /// Releases the read/write lock on MemoryFileData when disposed.
    /// </summary>
    internal class NonDisposableMemoryStream : Stream
    {
        private readonly MemoryStream _stream;
        private readonly MemoryFileData _fileData;
        private readonly bool _isWriter;
        private bool _released;

        public NonDisposableMemoryStream(MemoryFileData fileData, bool isWriter)
        {
            _fileData = fileData;
            _stream = fileData.Stream;
            _isWriter = isWriter;
        }

        public override bool CanRead => !_isWriter && _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _isWriter && _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position
        {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public override void Flush() => _stream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_isWriter)
                throw new NotSupportedException("This stream was opened for writing.");
            return _stream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

        public override void SetLength(long value)
        {
            if (!_isWriter)
                throw new NotSupportedException("This stream was opened for reading.");
            _stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_isWriter)
                throw new NotSupportedException("This stream was opened for reading.");
            _stream.Write(buffer, offset, count);
            _fileData.LastWriteTimeUtc = DateTime.UtcNow;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_released && disposing)
            {
                _released = true;
                if (_isWriter)
                    _fileData.ReleaseWrite();
                else
                    _fileData.ReleaseRead();
            }
            // Deliberately do NOT dispose _stream — it is owned by MemoryFileData.
            base.Dispose(disposing);
        }
    }
}
