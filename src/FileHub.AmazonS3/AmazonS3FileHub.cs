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
            var rootPrefix = PathUtil.NormalizePrefix(rootPath);
            Root = new AmazonS3Directory(_session, rootPrefix, pathMode);
        }

        // === Canonical factory: options bag (preferred from v0.next onward) ===

        /// <summary>
        /// Build a FileHub from an <see cref="S3HubOptions"/> bag. This is the
        /// canonical entry point: it covers profile-, credentials-, and
        /// client-based construction in one place. The legacy <c>From*</c>
        /// methods remain for source compatibility but are marked
        /// <see cref="ObsoleteAttribute"/> in favour of this overload.
        /// </summary>
        public static AmazonS3FileHub Create(S3HubOptions options)
            => SyncBridge.Run(ct => CreateAsync(options, ct));

        /// <inheritdoc cref="Create(S3HubOptions)"/>
        public static Task<AmazonS3FileHub> CreateAsync(
            S3HubOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.BucketName))
                throw new ArgumentException("BucketName cannot be null or empty.", nameof(options));

            var strategies = (options.Client != null ? 1 : 0)
                           + (options.Credentials != null ? 1 : 0)
                           + (options.Profile != null ? 1 : 0);
            if (strategies > 1)
                throw new ArgumentException("S3HubOptions accepts exactly one of Client, Credentials, or Profile.", nameof(options));

            if (options.Client != null)
            {
                if (string.IsNullOrEmpty(options.Region))
                {
                    var fromClient = (options.Client as AmazonS3Client)?.Config?.RegionEndpoint?.SystemName;
                    if (string.IsNullOrEmpty(fromClient))
                        throw new ArgumentException("Region is required when Client is provided (unless the client is an AmazonS3Client with a RegionEndpoint).", nameof(options));
                    return BuildAsync(
                        new RealS3Client(options.Client, options.BucketName, fromClient, ownsClient: false),
                        options.RootPath, options.PathMode, cancellationToken);
                }
                return BuildAsync(
                    new RealS3Client(options.Client, options.BucketName, options.Region, ownsClient: false),
                    options.RootPath, options.PathMode, cancellationToken);
            }

            if (options.Credentials != null)
            {
                if (string.IsNullOrEmpty(options.Region))
                    throw new ArgumentException("Region is required when Credentials is provided.", nameof(options));
                var sdkClient = new AmazonS3Client(options.Credentials, RegionEndpoint.GetBySystemName(options.Region));
                return BuildAsync(
                    new RealS3Client(sdkClient, options.BucketName, options.Region, ownsClient: true),
                    options.RootPath, options.PathMode, cancellationToken);
            }

            // Profile path (also the default if everything is null)
            var profile = options.Profile ?? "default";
            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile, out var credsFromProfile))
                throw new ArgumentException($"AWS profile \"{profile}\" not found in the local credential store.", nameof(options));

            string resolvedRegion = options.Region;
            if (string.IsNullOrEmpty(resolvedRegion)
                && chain.TryGetProfile(profile, out var cp)
                && cp.Region != null)
            {
                resolvedRegion = cp.Region.SystemName;
            }
            if (string.IsNullOrEmpty(resolvedRegion))
                throw new ArgumentException("Region is required when the profile does not carry one.", nameof(options));

            var sdk = new AmazonS3Client(credsFromProfile, RegionEndpoint.GetBySystemName(resolvedRegion));
            return BuildAsync(
                new RealS3Client(sdk, options.BucketName, resolvedRegion, ownsClient: true),
                options.RootPath, options.PathMode, cancellationToken);
        }

        // === Legacy factories: thin wrappers over Create. Will be removed in v1. ===

        /// <summary>
        /// Build a FileHub using a profile from <c>~/.aws/credentials</c>.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromProfile(bucketName, profile, region, rootPath)) instead. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromProfile(
            string rootPath,
            string bucketName,
            string profile = "default",
            string region = null,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Profile = profile,
                Region = region,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromProfile(bucketName, profile, region, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromProfileAsync(
            string rootPath,
            string bucketName,
            string profile = "default",
            string region = null,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Profile = profile,
                Region = region,
                PathMode = pathMode,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub from explicit AWS credentials and region.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromCredentials(bucketName, credentials, region, rootPath)) instead. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromCredentials(
            string rootPath,
            string bucketName,
            AWSCredentials credentials,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Credentials = credentials,
                Region = region,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromCredentials(bucketName, credentials, region, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromCredentialsAsync(
            string rootPath,
            string bucketName,
            AWSCredentials credentials,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Credentials = credentials,
                Region = region,
                PathMode = pathMode,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="IAmazonS3"/>.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromClient(bucketName, client, region, rootPath)) instead. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromClient(
            string bucketName,
            string rootPath,
            IAmazonS3 client,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                Region = region,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromClient(bucketName, client, region, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            IAmazonS3 client,
            string region,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                Region = region,
                PathMode = pathMode,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="AmazonS3Client"/>.
        /// Region is read from the client's <c>Config.RegionEndpoint</c>.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromClient(bucketName, AmazonS3Client, rootPath)) — region is read from client.Config.RegionEndpoint. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromClient(
            string bucketName,
            string rootPath,
            AmazonS3Client client,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromClient(bucketName, AmazonS3Client, rootPath), ct) — region is read from client.Config.RegionEndpoint. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            AmazonS3Client client,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                PathMode = pathMode,
            }, cancellationToken);

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
            var normalized = PathUtil.NormalizePrefix(rootPath);
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
