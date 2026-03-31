using System;
using System.IO;

namespace FileHub.Memory
{
    internal class NonDisposableMemoryStream : Stream
    {
        private readonly MemoryStream _stream;
        private readonly MemoryFileData _fileData;

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }

        public NonDisposableMemoryStream(MemoryFileData fileData, MemoryStream stream)
        {
            _fileData = fileData;
            _stream = stream;
        }

        public override void Flush()
        {
            _stream.Flush();
            _fileData.LastWriteTimeUtc = DateTime.UtcNow;
        }

        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _stream.Write(buffer, offset, count);
        }
    }
}
