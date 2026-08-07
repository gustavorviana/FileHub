using FileHub.Ftp.Internal;
using FluentFTP;
using System;
using System.Threading;
using System.Threading.Tasks;

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

        public DirectoryEntry Root { get; }

        private FtpFileHub(FtpSession session, string rootPath)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var normalizedRoot = FtpPathUtil.NormalizeRoot(rootPath);
            Root = new FtpDirectory(_session, normalizedRoot);
        }

        // === Public factory: a single options-object entry point (Create) with
        // its sync sibling. All connection + FTPS configuration lives on
        // FtpHubOptions, so there is no sync/async factory sprawl. ===

        /// <summary>
        /// Build a hub from <see cref="FtpHubOptions"/> — the single entry point,
        /// carrying connection, root, and FTPS (TLS) configuration. Prefer the
        /// typed <c>FtpHubOptions.From*</c> factories. Blocks the calling thread —
        /// prefer <see cref="CreateAsync"/> under a <c>SynchronizationContext</c>
        /// (UI, ASP.NET classic).
        /// </summary>
        public static FtpFileHub Create(FtpHubOptions options)
            => SyncBridge.Run(ct => CreateAsync(options, ct));

        /// <inheritdoc cref="Create(FtpHubOptions)"/>
        public static Task<FtpFileHub> CreateAsync(
            FtpHubOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            RealFtpClient real;
            if (options.Client != null)
            {
                real = new RealFtpClient(options.Client, options.OwnsClient);
            }
            else
            {
                if (string.IsNullOrEmpty(options.Host))
                    throw new ArgumentException("Host cannot be null or empty.", nameof(options));
                if (options.Port <= 0 || options.Port > 65535)
                    throw new ArgumentOutOfRangeException(nameof(options), "Port must be in the range 1-65535.");

                real = new RealFtpClient(BuildClient(options), ownsClient: true);
            }

            return BuildAsync(real, options.RootPath, cancellationToken);
        }

        private static AsyncFtpClient BuildClient(FtpHubOptions o)
        {
            var client = new AsyncFtpClient(o.Host, o.User ?? "anonymous", o.Password ?? string.Empty, o.Port);
            if (o.Encryption != FtpEncryptionMode.None)
            {
                client.Config.EncryptionMode = o.Encryption;
                client.Config.DataConnectionEncryption = o.DataConnectionEncryption;

                var validate = o.CertificateValidation;
                if (validate != null)
                    // Adapt FluentFTP's validation event to the BCL callback.
                    client.ValidateCertificate += (control, e) =>
                        e.Accept = validate(control, e.Certificate, e.Chain, e.PolicyErrors);
            }
            return client;
        }

        // === Internal factories (used by tests with an in-memory fake) ===

        internal static FtpFileHub FromFtpClient(
            FileHub.Ftp.Internal.IFtpClient client,
            string rootPath = "/")
            => SyncBridge.Run(ct => FromFtpClientAsync(client, rootPath, ct));

        internal static Task<FtpFileHub> FromFtpClientAsync(
            FileHub.Ftp.Internal.IFtpClient client,
            string rootPath = "/",
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return BuildAsync(client, rootPath, cancellationToken);
        }

        private static async Task<FtpFileHub> BuildAsync(
            FileHub.Ftp.Internal.IFtpClient client,
            string rootPath,
            CancellationToken cancellationToken)
        {
            var hub = new FtpFileHub(new FtpSession(client), rootPath);
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
