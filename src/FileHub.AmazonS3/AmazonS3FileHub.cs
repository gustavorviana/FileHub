using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using FileHub.AmazonS3.Internal;
using System;
using System.Threading;
using System.Threading.Tasks;

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

        private AmazonS3FileHub(S3Session session, string rootPath)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var rootPrefix = PathUtil.NormalizePrefix(rootPath);
            Root = new AmazonS3Directory(_session, rootPrefix);
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
            ValidateMultipartOptions(options.Multipart, nameof(options));

            if (options.Client != null && options.Credentials != null && options.Profile != null)
                throw new ArgumentException("S3HubOptions accepts exactly one of Client, Credentials, or Profile.", nameof(options));

            if (options.Client != null && options.SdkConfig != null)
                throw new ArgumentException("SdkConfig only applies when the hub creates the client; an external Client already carries its own configuration.", nameof(options));

            if (options.Client != null)
            {
                if (string.IsNullOrEmpty(options.Region))
                {
                    var fromClient = (options.Client as AmazonS3Client)?.Config?.RegionEndpoint?.SystemName;
                    if (string.IsNullOrEmpty(fromClient))
                        throw new ArgumentException("Region is required when Client is provided (unless the client is an AmazonS3Client with a RegionEndpoint).", nameof(options));
                    return BuildAsync(
                        new RealS3Client(options.Client, options.BucketName, fromClient, ownsClient: false),
                        options.RootPath, cancellationToken, options.Multipart);
                }
                return BuildAsync(
                    new RealS3Client(options.Client, options.BucketName, options.Region, ownsClient: false),
                    options.RootPath, cancellationToken, options.Multipart);
            }

            if (options.Credentials != null)
            {
                var region = !string.IsNullOrEmpty(options.Region)
                    ? options.Region
                    : options.SdkConfig?.RegionEndpoint?.SystemName;
                if (string.IsNullOrEmpty(region))
                    throw new ArgumentException("Region is required when Credentials is provided (set Region or SdkConfig.RegionEndpoint).", nameof(options));
                var sdkClient = new AmazonS3Client(options.Credentials, PrepareSdkConfig(options.SdkConfig, region));
                return BuildAsync(
                    new RealS3Client(sdkClient, options.BucketName, region, ownsClient: true),
                    options.RootPath, cancellationToken, options.Multipart);
            }

            // Profile path (also the default if everything is null)
            var profile = options.Profile ?? "default";
            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile, out var credsFromProfile))
                throw new ArgumentException($"AWS profile \"{profile}\" not found in the local credential store.", nameof(options));

            string resolvedRegion = options.Region;
            if (string.IsNullOrEmpty(resolvedRegion))
                resolvedRegion = options.SdkConfig?.RegionEndpoint?.SystemName;

            if (string.IsNullOrEmpty(resolvedRegion)
                && chain.TryGetProfile(profile, out var cp)
                && cp.Region != null)
            {
                resolvedRegion = cp.Region.SystemName;
            }

            if (string.IsNullOrEmpty(resolvedRegion))
                throw new ArgumentException("Region is required when the profile does not carry one.", nameof(options));

            var sdk = new AmazonS3Client(credsFromProfile, PrepareSdkConfig(options.SdkConfig, resolvedRegion));
            return BuildAsync(
                new RealS3Client(sdk, options.BucketName, resolvedRegion, ownsClient: true),
                options.RootPath, cancellationToken, options.Multipart);
        }

        /// <summary>
        /// Config for hub-owned SDK clients: the consumer-supplied
        /// <see cref="S3HubOptions.SdkConfig"/> taken as-is (the hub pins
        /// nothing — retry, timeouts, proxy all stay whatever the consumer or
        /// the SDK defaults say), or a plain default config when none was
        /// supplied. The resolved region is always assigned on top: the
        /// <c>RegionEndpoint</c> getter falls back to the host environment
        /// (<c>AWS_REGION</c>, profiles) when unset, so "fill only when
        /// missing" cannot be detected reliably and an env region could
        /// silently win over the resolved one.
        /// </summary>
        private static AmazonS3Config PrepareSdkConfig(AmazonS3Config supplied, string region)
        {
            var config = supplied ?? new AmazonS3Config();
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            return config;
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
            string region = null)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Profile = profile,
                Region = region,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromProfile(bucketName, profile, region, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromProfileAsync(
            string rootPath,
            string bucketName,
            string profile = "default",
            string region = null,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Profile = profile,
                Region = region,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub from explicit AWS credentials and region.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromCredentials(bucketName, credentials, region, rootPath)) instead. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromCredentials(
            string rootPath,
            string bucketName,
            AWSCredentials credentials,
            string region)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Credentials = credentials,
                Region = region,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromCredentials(bucketName, credentials, region, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromCredentialsAsync(
            string rootPath,
            string bucketName,
            AWSCredentials credentials,
            string region,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Credentials = credentials,
                Region = region,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="IAmazonS3"/>.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromClient(bucketName, client, region, rootPath)) instead. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromClient(
            string bucketName,
            string rootPath,
            IAmazonS3 client,
            string region)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                Region = region,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromClient(bucketName, client, region, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            IAmazonS3 client,
            string region,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                Region = region,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="AmazonS3Client"/>.
        /// Region is read from the client's <c>Config.RegionEndpoint</c>.
        /// </summary>
        [Obsolete("Use Create(S3HubOptions.FromClient(bucketName, AmazonS3Client, rootPath)) — region is read from client.Config.RegionEndpoint. This overload will be removed in v1.")]
        public static AmazonS3FileHub FromClient(
            string bucketName,
            string rootPath,
            AmazonS3Client client)
            => Create(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
            });

        [Obsolete("Use CreateAsync(S3HubOptions.FromClient(bucketName, AmazonS3Client, rootPath), ct) — region is read from client.Config.RegionEndpoint. This overload will be removed in v1.")]
        public static Task<AmazonS3FileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            AmazonS3Client client,
            CancellationToken cancellationToken = default)
            => CreateAsync(new S3HubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
            }, cancellationToken);

        // === Internal factory (tests with in-memory fake) ===

        internal static AmazonS3FileHub FromS3Client(
            IS3Client client,
            string rootPath = "")
            => SyncBridge.Run(ct => FromS3ClientAsync(client, rootPath, ct));

        internal static Task<AmazonS3FileHub> FromS3ClientAsync(
            IS3Client client,
            string rootPath = "",
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return BuildAsync(client, rootPath, cancellationToken);
        }

        private static async Task<AmazonS3FileHub> BuildAsync(
            IS3Client client,
            string rootPath,
            CancellationToken cancellationToken,
            MultipartStreamOptions multipart = null)
        {
            multipart ??= MultipartStreamOptions.Default;
            ValidateMultipartOptions(multipart, nameof(multipart));
            var hub = new AmazonS3FileHub(new S3Session(client, multipart), rootPath);
            var normalized = PathUtil.NormalizePrefix(rootPath);
            if (!string.IsNullOrEmpty(normalized) && hub.Root is IRefreshable refreshable)
                await refreshable.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return hub;
        }

        internal static void ValidateMultipartOptions(MultipartStreamOptions multipart, string parameterName)
        {
            if (multipart == null)
                return;

            if (multipart.Threshold <= 0)
                throw new ArgumentOutOfRangeException(parameterName, "MultipartThreshold must be positive.");

            if (multipart.PartSize < AmazonS3File.S3MinimumPartSize || multipart.PartSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, $"MultipartPartSize must be between {AmazonS3File.S3MinimumPartSize} and {int.MaxValue} bytes for the in-memory stream implementation.");

            if (multipart.Threshold > multipart.PartSize)
                throw new ArgumentException("MultipartThreshold cannot exceed MultipartPartSize.", parameterName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}
