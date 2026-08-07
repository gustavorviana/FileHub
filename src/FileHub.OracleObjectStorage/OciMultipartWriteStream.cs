using FileHub.OracleObjectStorage.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Accumulates incoming bytes into a configurable local part buffer; every time
    /// the buffer fills it fires <c>UploadPart</c> and resets. On close,
    /// the trailing buffer is uploaded and <c>CommitMultipartUpload</c>
    /// finalizes the object. Any exception during a write (or during
    /// close) aborts the upload so the store is not left with orphan
    /// parts.
    /// </summary>
    internal sealed class OciMultipartWriteStream : Stream
    {
        private const int MaximumPartCount = 10_000;

        private readonly List<OciCompletedPart> _completedParts = [];
        private readonly OracleObjectStorageFile _file;
        private readonly OciWritePayload _payload;
        private readonly MemoryStream _buffer;
        private readonly string _uploadId;
        private readonly int _partBufferSize;

        private int _nextPartNumber = 1;
        private bool _completed;
        private bool _aborted;
        private bool _disposed;
        private long _totalWritten;

        public OciMultipartWriteStream(OracleObjectStorageFile file, string uploadId, OciWritePayload payload, MemoryStream buffer, int partBufferSize)
        {
            _file = file ?? throw new ArgumentNullException(nameof(file));
            _uploadId = uploadId ?? throw new ArgumentNullException(nameof(uploadId));
            _partBufferSize = partBufferSize;

            _buffer = buffer ?? new MemoryStream();
            _totalWritten = _buffer.Length;
            _payload = payload;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && !_aborted;

        public override long Length => _totalWritten;
        public override long Position { get => _totalWritten; set => throw new NotSupportedException(); }

        public override void Flush()
        {
            ThrowIfUnwritable();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfUnwritable();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => SyncBridge.Run(ct => WriteAsync(buffer, offset, count, ct));

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfUnwritable();

            try
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (offset + count > buffer.Length)
                    throw new ArgumentException("offset + count exceeds buffer length.", nameof(buffer));
                if (count == 0) return;

                cancellationToken.ThrowIfCancellationRequested();

                int written = 0;
                while (written < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var space = _partBufferSize - (int)_buffer.Length;
                    var chunk = Math.Min(space, count - written);
                    _buffer.Write(buffer, offset + written, chunk);
                    _totalWritten += chunk;
                    written += chunk;

                    if (_buffer.Length >= _partBufferSize)
                        await UploadCurrentBufferAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Any failure during a write leaves the stream in an undefined
                // state — abort so the store isn't left with orphan parts.
                await AbortQuietlyAsync().ConfigureAwait(false);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) { base.Dispose(disposing); return; }

            try
            {
                if (disposing && !_aborted && !_completed)
                {
                    try { SyncBridge.Run(ct => CompleteAsync(ct)); }
                    catch
                    {
                        // The abort must never replace the Complete failure —
                        // that's the exception the caller needs to see.
                        try { SyncBridge.Run(_ => AbortQuietlyAsync()); } catch { }
                        throw;
                    }
                }
            }
            finally
            {
                _disposed = true;
                _buffer.Dispose();
                base.Dispose(disposing);
            }
        }

#if NET8_0_OR_GREATER
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) { await base.DisposeAsync().ConfigureAwait(false); return; }

            try
            {
                if (!_aborted && !_completed)
                {
                    try { await CompleteAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { await AbortQuietlyAsync().ConfigureAwait(false); throw; }
                }
            }
            finally
            {
                _disposed = true;
                _buffer.Dispose();
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
#endif

        private async Task CompleteAsync(CancellationToken cancellationToken)
        {
            var client = _file.SessionInternal.Client;

            // OCI rejects CommitMultipartUpload with zero parts. If the caller
            // opened the stream but never wrote anything, abort the upload and
            // put a zero-byte object in its place so the resulting file still
            // exists — with the write options the upload was bound to.
            if (_completedParts.Count == 0 && _buffer.Length == 0)
            {
                await AbortQuietlyAsync().ConfigureAwait(false);
                using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
                await client.PutObjectAsync(
                    _file.ObjectName, empty, 0,
                    _payload.ServerOptions,
                    cancellationToken).ConfigureAwait(false);
                _file.OnWriteCommitted(0, _payload.TimestampUtc, _payload.Snapshot);
                return;
            }

            if (_buffer.Length > 0)
                await UploadCurrentBufferAsync(cancellationToken).ConfigureAwait(false);

            await client.CommitMultipartUploadAsync(_file.ObjectName, _uploadId, _completedParts, cancellationToken).ConfigureAwait(false);
            _completed = true;
            _file.OnWriteCommitted(_totalWritten, _payload.TimestampUtc, _payload.Snapshot);
        }

        private async Task UploadCurrentBufferAsync(CancellationToken cancellationToken)
        {
            if (_nextPartNumber > MaximumPartCount)
                throw new FileHubException($"OCI multipart upload exceeded the {MaximumPartCount:N0}-part limit. Increase MultipartStreamOptions.PartSize.");

            _buffer.Position = 0;
            var len = _buffer.Length;
            var client = _file.SessionInternal.Client;
            var etag = await client.UploadPartAsync(_file.ObjectName, _uploadId, _nextPartNumber, _buffer, len, cancellationToken).ConfigureAwait(false);
            _completedParts.Add(new OciCompletedPart { PartNumber = _nextPartNumber, ETag = etag });
            _nextPartNumber++;
            _buffer.SetLength(0);
            _buffer.Position = 0;
        }

        private async Task AbortQuietlyAsync()
        {
            if (_aborted || _completed) return;
            _aborted = true;
            try
            {
                await _file.SessionInternal.Client.AbortMultipartUploadAsync(_file.ObjectName, _uploadId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Abort is best-effort — original exception already on the stack.
            }
        }

        private void ThrowIfUnwritable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OciMultipartWriteStream));
            if (_aborted) throw new InvalidOperationException("Multipart upload was aborted after a previous write error.");
            if (_completed) throw new InvalidOperationException("Multipart upload already completed.");
        }
    }
}
