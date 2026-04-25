using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// <see cref="IFileHub"/> implementation backed by AWS S3. A FileHub
    /// instance is scoped to a single bucket; an optional <c>rootPath</c>
    /// narrows visibility to objects under a given prefix.
    /// </summary>
    public sealed class AmazonS3FileHub : IAmazonS3FileHub, IDisposable
    {
        private readonly S3Session _session;
        private bool _disposed;

        public FileDirectory Root { get; }

        private AmazonS3FileHub(S3Session session, string rootPath, DirectoryPathMode pathMode)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var rootPrefix = S3PathUtil.NormalizePrefix(rootPath);
            Root = new AmazonS3Directory(_session, rootPrefix, pathMode);
        }

        // === Factories: sync delegates to async at the boundary ===

        /// <summary>
        /// Build a FileHub using a profile from <c>~/.aws/credentials</c>.
        /// <paramref name="region"/> is required when the profile does not
        /// carry one. Defaults to <see cref="DirectoryPathMode.Direct"/>.
        /// </summary>
        public static AmazonS3FileHub FromProfile(
            string rootPath,
            string bucketName,
            string profile = "default",
            string region = null,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => SyncBridge.Run(ct => FromProfileAsync(rootPath, bucketName, profile, region, pathMode, ct));

        public static Task<AmazonS3FileHub> FromProfileAsync(
            string rootPath,
            string bucketName,
            string profile = "default",
            string region = null,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));

            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile ?? "default", out var credentials))
                throw new ArgumentException($"AWS profile \"{profile}\" not found in the local credential store.", nameof(profile));

            string resolvedRegion = region;
            if (string.IsNullOrEmpty(resolvedRegion)
                && chain.TryGetProfile(profile ?? "default", out var cp)
                && cp.Region != null)
            {
                resolvedRegion = cp.Region.SystemName;
            }

            if (string.IsNullOrEmpty(resolvedRegion))
                throw new ArgumentException("Region is required when the profile does not carry one.", nameof(region));

            return FromCredentialsAsync(rootPath, bucketName, credentials, resolvedRegion, pathMode, cancellationToken);
        }

        /// <summary>
        /// Build a FileHub from explicit AWS credentials and region.
        /// Covers IAM role, env vars, instance profile, and any other
        /// <see cref="AWSCredentials"/>-derived provider.
        /// </summary>
        public static AmazonS3FileHub FromCredentials(
            string rootPath,
            string bucketName,
            AWSCredentials credentials,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => SyncBridge.Run(ct => FromCredentialsAsync(rootPath, bucketName, credentials, region, pathMode, ct));

        public static Task<AmazonS3FileHub> FromCredentialsAsync(
            string rootPath,
            string bucketName,
            AWSCredentials credentials,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            if (string.IsNullOrEmpty(region))
                throw new ArgumentException("Region cannot be null or empty.", nameof(region));

            var sdkClient = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(region));
            var real = new RealS3Client(sdkClient, bucketName, region, ownsClient: true);
            return BuildAsync(real, rootPath, pathMode, cancellationToken);
        }

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="IAmazonS3"/>.
        /// The caller retains ownership of the client — disposing this
        /// FileHub does <b>not</b> dispose it.
        /// </summary>
        public static AmazonS3FileHub FromClient(
            string bucketName,
            string rootPath,
            IAmazonS3 client,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => SyncBridge.Run(ct => FromClientAsync(bucketName, rootPath, client, region, pathMode, ct));

        public static Task<AmazonS3FileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            IAmazonS3 client,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(region))
                throw new ArgumentException("Region cannot be null or empty.", nameof(region));

            var real = new RealS3Client(client, bucketName, region, ownsClient: false);
            return BuildAsync(real, rootPath, pathMode, cancellationToken);
        }

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="AmazonS3Client"/>.
        /// The region is taken from the client's <c>Config.RegionEndpoint</c>,
        /// so callers don't need to repeat it. The caller retains ownership
        /// of the client — disposing this FileHub does <b>not</b> dispose it.
        /// </summary>
        public static AmazonS3FileHub FromClient(
            string bucketName,
            string rootPath,
            AmazonS3Client client,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => SyncBridge.Run(ct => FromClientAsync(bucketName, rootPath, client, pathMode, ct));

        public static Task<AmazonS3FileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            AmazonS3Client client,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            var region = client.Config?.RegionEndpoint?.SystemName
                ?? throw new ArgumentException("AmazonS3Client must have a RegionEndpoint configured.", nameof(client));
            return FromClientAsync(bucketName, rootPath, (IAmazonS3)client, region, pathMode, cancellationToken);
        }

        // === Internal factory (tests with in-memory fake) ===

        internal static AmazonS3FileHub FromS3Client(
            IS3Client client,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => SyncBridge.Run(ct => FromS3ClientAsync(client, rootPath, pathMode, ct));

        internal static Task<AmazonS3FileHub> FromS3ClientAsync(
            IS3Client client,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return BuildAsync(client, rootPath, pathMode, cancellationToken);
        }

        private static async Task<AmazonS3FileHub> BuildAsync(
            IS3Client client,
            string rootPath,
            DirectoryPathMode pathMode,
            CancellationToken cancellationToken)
        {
            var hub = new AmazonS3FileHub(new S3Session(client), rootPath, pathMode);
            var normalized = S3PathUtil.NormalizePrefix(rootPath);
            if (!string.IsNullOrEmpty(normalized) && hub.Root is IRefreshable refreshable)
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
