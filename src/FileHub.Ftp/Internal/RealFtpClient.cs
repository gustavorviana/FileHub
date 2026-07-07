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
                return new GateHoldingStream(raw, _gate, ct => ConsumeTransferReplyAsync(path, ct));
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
                return new GateHoldingStream(raw, _gate, ct => ConsumeTransferReplyAsync(path, ct));
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
            private readonly Stream _inner;
            private readonly SemaphoreSlim _gate;
            private readonly Func<CancellationToken, Task> _onClosed;
            private int _released;

            public GateHoldingStream(Stream inner, SemaphoreSlim gate, Func<CancellationToken, Task> onClosed)
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
                => SyncBridge.Run(ct => _inner.WriteAsync(buffer, offset, count, ct));
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.WriteAsync(buffer, offset, count, cancellationToken);
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
                        _inner.Dispose();
                        // Must run before the gate opens: the reply belongs to
                        // THIS transfer, and the next command must not race it.
                        SyncBridge.Run(ct => _onClosed(ct));
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
                    await _inner.DisposeAsync().ConfigureAwait(false);
                    await _onClosed(CancellationToken.None).ConfigureAwait(false);
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
