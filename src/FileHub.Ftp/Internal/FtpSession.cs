using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Ftp.Internal
{
    /// <summary>
    /// Per-FileHub shared state. Owns the <see cref="IFtpClient"/> and gates
    /// the lazy connect on first use so concurrent operations don't all race
    /// to <c>CONNECT</c>.
    /// </summary>
    internal interface IFtpSession : IDisposable
    {
        IFtpClient Client { get; }

        Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
    }

    internal static class FtpSessionTarget
    {
        /// <summary>
        /// Returns true when both clients share the same FTP connection scope.
        /// FTP rename (<c>RNFR/RNTO</c>) only works through a single
        /// authenticated control channel, so a shared scope is the correct
        /// gate for "can the driver server-side rename across directories?".
        /// </summary>
        public static bool SameConnection(IFtpClient a, IFtpClient b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return ReferenceEquals(a.ConnectionScope, b.ConnectionScope);
        }
    }

    internal sealed class FtpSession : IFtpSession
    {
        // 0 = unconnected, 1 = connected. Volatile reads short-circuit the
        // semaphore once a single connect has succeeded.
        private int _connected;
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public IFtpClient Client { get; }

        public FtpSession(IFtpClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            // Cheap fast-path: trust _connected only as long as the underlying
            // control channel is still up. FTP servers commonly drop idle
            // connections after a few minutes — without this check the hub
            // would stay marked "connected" and every operation would fail
            // with a stale-channel error until the user recreated the hub.
            if (Volatile.Read(ref _connected) == 1 && Client.IsConnected) return;

            await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _connected) == 1 && Client.IsConnected) return;
                ThrowIfDisposed();
                await Client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _connected, 1);
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connectGate.Dispose();
            Client.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FtpSession));
        }
    }
}
