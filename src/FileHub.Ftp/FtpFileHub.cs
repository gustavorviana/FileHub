using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FileHub.Ftp.Internal;
using FluentFTP;

namespace FileHub.Ftp
{
    /// <summary>
    /// <see cref="IFileHub"/> implementation backed by an FTP server. A
    /// FileHub instance is scoped to a single FTP connection; an optional
    /// <c>rootPath</c> narrows visibility to objects under a given absolute
    /// path on the server.
    /// </summary>
    public sealed class FtpFileHub : IFtpFileHub, IDisposable
    {
        private readonly FtpSession _session;
        private bool _disposed;

        public FileDirectory Root { get; }

        private FtpFileHub(FtpSession session, string rootPath, DirectoryPathMode pathMode)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var normalizedRoot = FtpPathUtil.NormalizeRoot(rootPath);
            Root = new FtpDirectory(_session, normalizedRoot, pathMode);
        }

        // === Public factories: sync delegates to async at the top-level boundary ===

        /// <summary>
        /// Open a fresh FTP connection to <paramref name="host"/>:<paramref name="port"/>
        /// and build a hub rooted at <paramref name="rootPath"/>. Ensures the
        /// root directory exists on the server (creating it if needed).
        /// Blocks the calling thread — prefer <see cref="ConnectAsync"/> under
        /// a <c>SynchronizationContext</c> (UI, ASP.NET classic).
        /// </summary>
        public static FtpFileHub Connect(
            string host,
            int port = 21,
            string user = "anonymous",
            string password = "",
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates)
            => ConnectAsync(host, port, user, password, rootPath, pathMode).GetAwaiter().GetResult();

        public static async Task<FtpFileHub> ConnectAsync(
            string host,
            int port = 21,
            string user = "anonymous",
            string password = "",
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(host))
                throw new ArgumentException("Host cannot be null or empty.", nameof(host));
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be in the range 1-65535.");

            var client = new AsyncFtpClient(host, user ?? "anonymous", password ?? string.Empty, port);
            var real = new RealFtpClient(client, ownsClient: true);
            return await BuildAsync(real, rootPath, pathMode, cancellationToken).ConfigureAwait(false);
        }

        public static FtpFileHub FromCredentials(
            string host,
            int port,
            NetworkCredential credentials,
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates)
            => FromCredentialsAsync(host, port, credentials, rootPath, pathMode).GetAwaiter().GetResult();

        public static Task<FtpFileHub> FromCredentialsAsync(
            string host,
            int port,
            NetworkCredential credentials,
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates,
            CancellationToken cancellationToken = default)
        {
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            return ConnectAsync(host, port, credentials.UserName, credentials.Password, rootPath, pathMode, cancellationToken);
        }

        /// <summary>
        /// Build a hub around an externally-owned <see cref="AsyncFtpClient"/>.
        /// The caller retains ownership of the client — disposing this hub
        /// does <b>not</b> dispose it.
        /// </summary>
        public static FtpFileHub FromClient(
            AsyncFtpClient client,
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates)
            => FromClientAsync(client, rootPath, pathMode).GetAwaiter().GetResult();

        public static Task<FtpFileHub> FromClientAsync(
            AsyncFtpClient client,
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            var real = new RealFtpClient(client, ownsClient: false);
            return BuildAsync(real, rootPath, pathMode, cancellationToken);
        }

        // === Internal factories (used by tests with an in-memory fake) ===

        internal static FtpFileHub FromFtpClient(
            FileHub.Ftp.Internal.IFtpClient client,
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates)
            => FromFtpClientAsync(client, rootPath, pathMode).GetAwaiter().GetResult();

        internal static Task<FtpFileHub> FromFtpClientAsync(
            FileHub.Ftp.Internal.IFtpClient client,
            string rootPath = "/",
            DirectoryPathMode pathMode = DirectoryPathMode.OpenIntermediates,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return BuildAsync(client, rootPath, pathMode, cancellationToken);
        }

        private static async Task<FtpFileHub> BuildAsync(
            FileHub.Ftp.Internal.IFtpClient client,
            string rootPath,
            DirectoryPathMode pathMode,
            CancellationToken cancellationToken)
        {
            var hub = new FtpFileHub(new FtpSession(client), rootPath, pathMode);
            var normalized = FtpPathUtil.NormalizeRoot(rootPath);
            if (normalized != "/" && hub.Root is IRefreshable refreshable)
                await refreshable.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return hub;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}
