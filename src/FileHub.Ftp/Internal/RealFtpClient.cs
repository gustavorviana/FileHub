using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using FluentFTP.Exceptions;

namespace FileHub.Ftp.Internal
{
    /// <summary>
    /// <see cref="IFtpClient"/> implementation backed by FluentFTP's
    /// <see cref="AsyncFtpClient"/>. All FluentFTP-specific exceptions are
    /// translated into BCL / FileHub exceptions inside this class so consumers
    /// only see <see cref="FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="FileHubException"/>.
    /// <para>
    /// Every operation is serialized on a per-connection gate: FTP multiplexes
    /// all commands over a single control channel and allows one data transfer
    /// at a time, so concurrent calls on the same connection would interleave
    /// protocol commands and corrupt the session. The gate is keyed on the
    /// underlying <see cref="AsyncFtpClient"/>, so hubs sharing an
    /// externally-owned client contend on the same gate. Streams returned by
    /// <see cref="OpenReadAsync"/> / <see cref="OpenWriteAsync"/> hold the
    /// gate until disposed — the data channel is busy for the whole transfer,
    /// not just the OPEN command.
    /// </para>
    /// </summary>
    internal sealed class RealFtpClient : IFtpClient
    {
        // FTP completion code for "550 Requested action not taken: file unavailable".
        // FluentFTP surfaces it through FtpCommandException.CompletionCode.
        private const string NotFoundCode = "550";

        // One gate per physical connection. Never disposed: SemaphoreSlim
        // without AvailableWaitHandle holds no unmanaged state, and letting
        // the table release it with the client avoids the
        // dispose-while-waiters-queued race entirely.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AsyncFtpClient, SemaphoreSlim> _gates =
            new System.Runtime.CompilerServices.ConditionalWeakTable<AsyncFtpClient, SemaphoreSlim>();

        private readonly AsyncFtpClient _client;
        private readonly SemaphoreSlim _gate;
        private readonly bool _ownsClient;
        private volatile bool _disposed;

        public object ConnectionScope => _client;

        public bool IsConnected => !_disposed && _client.IsConnected;

        public RealFtpClient(AsyncFtpClient client, bool ownsClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
            _gate = _gates.GetValue(client, static _ => new SemaphoreSlim(1, 1));
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_client.IsConnected) return Task.CompletedTask;
            return RunSerializedAsync("<connect>", async ct =>
            {
                await _client.Connect(ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<FtpItemInfo> StatAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                var item = await _client.GetObjectInfo(path, true, ct).ConfigureAwait(false);
                return item == null ? null : ToInfo(item);
            }, cancellationToken);
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                return await _client.FileExists(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                return await _client.DirectoryExists(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var raw = await TranslateAsync(path, async ct =>
                {
                    return (Stream)await _client.OpenRead(path, FtpDataType.Binary, offset, true, ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                return new GateHoldingStream(raw, _gate, (_, ct) => ConsumeTransferReplyAsync(path, ct));
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        public async Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var raw = await TranslateAsync(path, async ct =>
                {
                    return (Stream)await _client.OpenWrite(path, FtpDataType.Binary, false, ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                return new GateHoldingStream(raw, _gate, (written, ct) => ConsumeAndVerifyWriteAsync(path, written, ct));
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        /// <summary>
        /// FluentFTP contract: after a stream from <c>OpenRead</c>/<c>OpenWrite</c>
        /// is disposed, the final transfer reply ("226 Transfer complete") MUST
        /// be read off the control channel. Skipping it leaves the reply stale
        /// on the socket — the next command desynchronizes the protocol and the
        /// server aborts the tail of the transfer (observed as silent
        /// truncation). A non-success reply means the server did not commit
        /// the transfer, so it surfaces as an exception.
        /// </summary>
        private async Task ConsumeTransferReplyAsync(string contextPath, CancellationToken cancellationToken)
        {
            var reply = await _client.GetReply(cancellationToken).ConfigureAwait(false);
            if (!reply.Success)
                throw new FileHubException(
                    $"FTP transfer for \"{contextPath}\" did not complete: {reply.Code} {reply.Message}");
        }

        /// <summary>
        /// Write-stream close: consume the transfer reply, then confirm the
        /// server actually stored every byte we sent. A single FTP data channel
        /// can silently drop its tail (the STOR still ends with a success reply)
        /// when it lands on stale passive-port state; the SIZE probe here — run
        /// while the command gate is still held, so it cannot race the next
        /// operation — turns that silent truncation into a loud
        /// <see cref="FtpTransferTruncatedException"/> the caller can retry.
        /// </summary>
        private async Task ConsumeAndVerifyWriteAsync(string contextPath, long bytesWritten, CancellationToken cancellationToken)
        {
            await ConsumeTransferReplyAsync(contextPath, cancellationToken).ConfigureAwait(false);

            var info = await _client.GetObjectInfo(contextPath, true, cancellationToken).ConfigureAwait(false);
            var stored = info?.Size ?? -1;
            if (stored != bytesWritten)
                throw new FtpTransferTruncatedException(contextPath, bytesWritten, stored);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                await _client.DeleteFile(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                await _client.DeleteDirectory(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(fromPath, async ct =>
            {
                await _client.Rename(fromPath, toPath, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task CreateDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                await _client.CreateDirectory(path, recursive, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<IReadOnlyList<FtpItemInfo>> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return RunSerializedAsync(path, async ct =>
            {
                var items = await _client.GetListing(path, ct).ConfigureAwait(false);
                if (items == null || items.Length == 0)
                    return (IReadOnlyList<FtpItemInfo>)Array.Empty<FtpItemInfo>();

                var result = new List<FtpItemInfo>(items.Length);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (item.Type != FtpObjectType.File && item.Type != FtpObjectType.Directory) continue;
                    result.Add(ToInfo(item));
                }
                return (IReadOnlyList<FtpItemInfo>)result;
            }, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsClient) _client.Dispose();
        }

        // --- Helpers ---

        private static FtpItemInfo ToInfo(FtpListItem item)
        {
            return new FtpItemInfo
            {
                FullPath = item.FullName,
                Name = item.Name,
                IsDirectory = item.Type == FtpObjectType.Directory,
                Size = item.Size < 0 ? 0 : item.Size,
                ModifiedUtc = item.Modified == DateTime.MinValue ? default : item.Modified.ToUniversalTime(),
                CreatedUtc = item.Created == DateTime.MinValue ? default : item.Created.ToUniversalTime()
            };
        }

        private async Task<T> RunSerializedAsync<T>(string contextPath, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await TranslateAsync(contextPath, work, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task RunSerializedAsync(string contextPath, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await TranslateAsync(contextPath, work, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<T> TranslateAsync<T>(string contextPath, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            catch (FtpException fe)
            {
                throw Translate(fe, contextPath);
            }
        }

        private async Task TranslateAsync(string contextPath, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (FtpException fe)
            {
                throw Translate(fe, contextPath);
            }
        }

        private Exception Translate(FtpException raw, string contextPath)
        {
            // Do not surface auth.Message: FTP servers echo parts of the
            // handshake (including the user) in it, and the string ends up in
            // logs. The inner exception keeps the detail for local debugging.
            if (raw is FtpAuthenticationException auth)
                return new UnauthorizedAccessException(
                    $"FTP authentication failed (path: \"{contextPath}\").",
                    auth);

            if (raw is FtpCommandException cmd)
            {
                if (string.Equals(cmd.CompletionCode, NotFoundCode, StringComparison.Ordinal)
                    || MessageIndicatesNotFound(cmd.Message))
                    return new FileNotFoundException(
                        $"FTP path \"{contextPath}\" was not found.",
                        cmd);
            }

            if (MessageIndicatesNotFound(raw.Message))
                return new FileNotFoundException(
                    $"FTP path \"{contextPath}\" was not found.",
                    raw);

            return new FileHubException(
                $"FTP operation failed for \"{contextPath}\": {raw.Message}",
                raw);
        }

        private static bool MessageIndicatesNotFound(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("no such file", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealFtpClient));
        }

        /// <summary>
        /// Holds the command gate for the lifetime of an FTP data transfer and
        /// consumes the final transfer reply when the stream closes (see
        /// <see cref="ConsumeTransferReplyAsync"/>). Releases the gate exactly
        /// once — on dispose, async dispose, or (last resort) finalization, so
        /// an abandoned stream cannot lock the connection forever.
        /// </summary>
        private sealed class GateHoldingStream : Stream
        {
            // Best-effort drain window inserted between the final FlushAsync and
            // the stream close (writes only). FluentFTP's async data stream is
            // buffered: FlushAsync returns before the last write's bytes have
            // actually reached the socket, so closing immediately sends the FIN
            // with the tail still in flight and the server stores a truncated
            // file (proven at the packet level — the client FINs after exactly
            // payload-64 KiB, no RST, no retransmit). A short yield lets that
            // pending send drain before the close; in a load test 0 ms truncated
            // ~93% of transfers while as little as 5 ms dropped it to 0%. This is
            // a mitigation, NOT a guarantee: the authoritative check is the
            // post-close SIZE verification in ConsumeAndVerifyWriteAsync, which
            // still throws FtpTransferTruncatedException if the tail was lost
            // anyway. FileHub's contract: try hard to deliver every byte, and if
            // it still cannot, fail loudly rather than corrupt silently.
            private static readonly TimeSpan PreCloseDrainDelay = TimeSpan.FromMilliseconds(50);

            private readonly Stream _inner;
            private readonly SemaphoreSlim _gate;
            private readonly Func<long, CancellationToken, Task> _onClosed;
            private long _bytesWritten;
            private int _released;

            public GateHoldingStream(Stream inner, SemaphoreSlim gate, Func<long, CancellationToken, Task> onClosed)
            {
                _inner = inner;
                _gate = gate;
                _onClosed = onClosed;
            }

            private void ReleaseOnce()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _gate.Release();
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            // Sync I/O routes through the async counterparts: FluentFTP's
            // AsyncFtpClient data streams are async-first, and their sync
            // Read/Write paths have been observed returning early EOF
            // (truncating transfers at socket-buffer boundaries).
            public override void Flush() => SyncBridge.Run(ct => _inner.FlushAsync(ct));
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count)
                => SyncBridge.Run(ct => _inner.ReadAsync(buffer, offset, count, ct));
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.ReadAsync(buffer, offset, count, cancellationToken);
            public override void Write(byte[] buffer, int offset, int count)
                => SyncBridge.Run(ct => WriteAsync(buffer, offset, count, ct));

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // Split into <=64 KiB writes. FluentFTP's data stream drops the
                // tail of a single large WriteAsync (a 256 KiB write lands as
                // 192 KiB — exactly one 64 KiB socket block short); feeding it
                // one socket-buffer-sized chunk at a time transfers every byte.
                const int MaxChunk = 64 * 1024;
                int written = 0;
                while (written < count)
                {
                    int chunk = Math.Min(MaxChunk, count - written);
                    await _inner.WriteAsync(buffer, offset + written, chunk, cancellationToken).ConfigureAwait(false);
                    written += chunk;
                }
                _bytesWritten += count;
            }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            protected override void Dispose(bool disposing)
            {
                if (_released != 0)
                {
                    base.Dispose(disposing);
                    return;
                }

                try
                {
                    if (disposing)
                    {
                        // Close via the ASYNC path even on a sync Dispose:
                        // FluentFTP's data stream is async-first, and its sync
                        // Dispose drops the final buffered block (observed as a
                        // 64 KiB tail truncation). Flush + DisposeAsync commits
                        // every byte. The reply read must run before the gate
                        // opens — the reply belongs to THIS transfer and the
                        // next command must not race it.
                        SyncBridge.Run(async ct =>
                        {
                            await _inner.FlushAsync(ct).ConfigureAwait(false);
                            if (_bytesWritten > 0)
                                await Task.Delay(PreCloseDrainDelay, ct).ConfigureAwait(false);
#if NET8_0_OR_GREATER
                            await _inner.DisposeAsync().ConfigureAwait(false);
#else
                            // netstandard2.0 has no Stream.DisposeAsync; the
                            // preceding async FlushAsync already drained the
                            // buffered tail, so a sync Dispose is safe here.
                            _inner.Dispose();
#endif
                            await _onClosed(_bytesWritten, ct).ConfigureAwait(false);
                        });
                    }
                }
                finally
                {
                    ReleaseOnce();
                    base.Dispose(disposing);
                }
            }

#if NET8_0_OR_GREATER
            public override async ValueTask DisposeAsync()
            {
                if (_released != 0)
                {
                    await base.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                try
                {
                    await _inner.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    if (_bytesWritten > 0)
                        await Task.Delay(PreCloseDrainDelay).ConfigureAwait(false);
                    await _inner.DisposeAsync().ConfigureAwait(false);
                    await _onClosed(_bytesWritten, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseOnce();
                    await base.DisposeAsync().ConfigureAwait(false);
                }
            }
#endif

            ~GateHoldingStream() => Dispose(false);
        }
    }
}
