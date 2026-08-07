using FluentFTP;
using System.Net;
using System.Net.Security;

namespace FileHub.Ftp
{
    /// <summary>
    /// Construction options for <see cref="FtpFileHub.Create(FtpHubOptions)"/>.
    /// Set either connection fields (<see cref="Host"/>, credentials) or an
    /// externally-owned <see cref="Client"/> — not both.
    /// <para>
    /// Prefer the typed <c>From*</c> static factories; the object initializer
    /// (<c>new FtpHubOptions { ... }</c>) stays available for advanced cases.
    /// </para>
    /// <code>
    /// var hub = FtpFileHub.Create(
    ///     FtpHubOptions.FromCredentials("ftp.example.com", user: "svc", password: "s3cret",
    ///         encryption: FtpEncryption.Explicit));
    /// </code>
    /// </summary>
    public sealed class FtpHubOptions
    {
        /// <summary>FTP server host. Required unless <see cref="Client"/> is set.</summary>
        public string Host { get; init; }

        /// <summary>Server port. Defaults to 21 (990 is typical for implicit FTPS).</summary>
        public int Port { get; init; } = 21;

        /// <summary>User name. Defaults to <c>"anonymous"</c>.</summary>
        public string User { get; init; } = "anonymous";

        /// <summary>Password. Defaults to empty.</summary>
        public string Password { get; init; } = "";

        /// <summary>Absolute path on the server the hub treats as its root. Defaults to <c>"/"</c>.</summary>
        public string RootPath { get; init; } = "/";

        /// <summary>
        /// TLS strategy (FTPS), using FluentFTP's <see cref="FtpEncryptionMode"/>.
        /// Defaults to <see cref="FtpEncryptionMode.None"/> (plain FTP).
        /// <see cref="FtpEncryptionMode.Explicit"/> is the common FTPES choice;
        /// <see cref="FtpEncryptionMode.Implicit"/> is usually port 990. Ignored
        /// when <see cref="Client"/> is supplied — an external client already
        /// carries its own encryption configuration.
        /// </summary>
        public FtpEncryptionMode Encryption { get; init; } = FtpEncryptionMode.None;

        /// <summary>
        /// When <see cref="Encryption"/> is not <see cref="FtpEncryptionMode.None"/>,
        /// also encrypt the data channel (file transfers), not just the control
        /// channel. Defaults to <c>true</c> — leaving data in plaintext under an
        /// otherwise-encrypted session is rarely what you want.
        /// </summary>
        public bool DataConnectionEncryption { get; init; } = true;

        /// <summary>
        /// Optional server-certificate validator for FTPS. When null, the
        /// underlying client's default validation applies (an invalid or
        /// untrusted certificate fails the connection). Supply a callback to
        /// accept self-signed certificates in development — returning
        /// <c>true</c> accepts the certificate. Ignored when
        /// <see cref="Encryption"/> is <see cref="FtpEncryptionMode.None"/> or when
        /// <see cref="Client"/> is supplied.
        /// </summary>
        public RemoteCertificateValidationCallback CertificateValidation { get; init; }

        /// <summary>
        /// Externally-owned FluentFTP client. Mutually exclusive with the
        /// connection fields above. Caller keeps ownership unless
        /// <see cref="OwnsClient"/> is set.
        /// </summary>
        public AsyncFtpClient Client { get; init; }

        /// <summary>
        /// When <see cref="Client"/> is set, whether the hub disposes it on its
        /// own disposal. Defaults to <c>false</c> (caller keeps ownership).
        /// </summary>
        public bool OwnsClient { get; init; }

        // === Typed factories ===

        /// <summary>Options for a fresh connection with inline credentials.</summary>
        public static FtpHubOptions FromCredentials(
            string host,
            int port = 21,
            string user = "anonymous",
            string password = "",
            string rootPath = "/",
            FtpEncryptionMode encryption = FtpEncryptionMode.None,
            RemoteCertificateValidationCallback certificateValidation = null)
            => new FtpHubOptions
            {
                Host = host,
                Port = port,
                User = user,
                Password = password,
                RootPath = rootPath,
                Encryption = encryption,
                CertificateValidation = certificateValidation,
            };

        /// <summary>Options for a fresh connection from a <see cref="NetworkCredential"/>.</summary>
        public static FtpHubOptions FromCredentials(
            string host,
            int port,
            NetworkCredential credentials,
            string rootPath = "/",
            FtpEncryptionMode encryption = FtpEncryptionMode.None,
            RemoteCertificateValidationCallback certificateValidation = null)
            => FromCredentials(
                host, port,
                credentials?.UserName ?? "anonymous",
                credentials?.Password ?? "",
                rootPath, encryption, certificateValidation);

        /// <summary>Options that reuse an externally-configured FluentFTP client.</summary>
        public static FtpHubOptions FromClient(
            AsyncFtpClient client,
            bool ownsClient = false,
            string rootPath = "/")
            => new FtpHubOptions
            {
                Client = client,
                OwnsClient = ownsClient,
                RootPath = rootPath,
            };
    }
}
