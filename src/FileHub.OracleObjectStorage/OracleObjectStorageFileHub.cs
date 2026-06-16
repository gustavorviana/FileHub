using System;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;
using Oci.Common;
using Oci.Common.Auth;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Requests;

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// <see cref="IFileHub"/> implementation backed by Oracle Cloud Infrastructure
    /// (OCI) Object Storage. A FileHub instance is scoped to a single bucket;
    /// an optional <c>rootPath</c> narrows visibility to objects under a given prefix.
    /// </summary>
    public sealed class OracleObjectStorageFileHub : IOracleObjectStorageFileHub, IDisposable
    {
        private readonly OciSession _session;
        private bool _disposed;

        public FileDirectory Root { get; }
        public FileHubFeatures Features { get; } = new FileHubFeatures(metadata: true);

        private OracleObjectStorageFileHub(OciSession session, string rootPath, DirectoryPathMode pathMode)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var rootPrefix = OciPathUtil.NormalizePrefix(rootPath);
            Root = new OracleObjectStorageDirectory(_session, rootPrefix, pathMode);
        }

        // === Canonical factory: options bag (preferred from v0.next onward) ===

        /// <summary>
        /// Build a FileHub from an <see cref="OciHubOptions"/> bag. This is the
        /// canonical entry point: covers config-file, provider-, and
        /// client-based construction in one place. The legacy <c>From*</c>
        /// methods remain for source compatibility but are marked
        /// <see cref="ObsoleteAttribute"/> in favour of this overload.
        /// </summary>
        public static OracleObjectStorageFileHub Create(OciHubOptions options)
            => SyncBridge.Run(ct => CreateAsync(options, ct));

        /// <inheritdoc cref="Create(OciHubOptions)"/>
        public static async Task<OracleObjectStorageFileHub> CreateAsync(
            OciHubOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.BucketName))
                throw new ArgumentException("BucketName cannot be null or empty.", nameof(options));

            var strategies = (options.Client != null ? 1 : 0)
                           + (options.Provider != null ? 1 : 0)
                           + (options.ConfigFilePath != null || options.Profile != null ? 1 : 0);
            // The "config file" strategy is also the default when nothing is set.

            if (strategies > 1)
                throw new ArgumentException("OciHubOptions accepts exactly one of Client, Provider, or (ConfigFilePath/Profile).", nameof(options));

            if (options.Client != null)
            {
                if (string.IsNullOrEmpty(options.RegionId))
                    throw new ArgumentException("RegionId is required when Client is provided.", nameof(options));
                if (string.IsNullOrEmpty(options.Namespace))
                    throw new ArgumentException("Namespace is required when Client is provided.", nameof(options));
                var real = new RealOciClient(options.Client, options.Namespace, options.BucketName, options.RegionId, ownsClient: false);
                return await BuildAsync(real, options.RootPath, options.PathMode, cancellationToken).ConfigureAwait(false);
            }

            IAuthenticationDetailsProvider provider;
            string regionId;
            if (options.Provider != null)
            {
                provider = options.Provider;
                if (!string.IsNullOrEmpty(options.RegionId))
                {
                    regionId = options.RegionId;
                }
                else if (provider is ConfigFileAuthenticationDetailsProvider cfgProvider && cfgProvider.Region != null)
                {
                    regionId = cfgProvider.Region.RegionId;
                }
                else
                {
                    throw new ArgumentException("RegionId is required when Provider does not carry one.", nameof(options));
                }
            }
            else
            {
                var profile = options.Profile ?? "DEFAULT";
                var configProvider = string.IsNullOrEmpty(options.ConfigFilePath)
                    ? new ConfigFileAuthenticationDetailsProvider(profile)
                    : new ConfigFileAuthenticationDetailsProvider(options.ConfigFilePath, profile);
                provider = configProvider;
                regionId = !string.IsNullOrEmpty(options.RegionId)
                    ? options.RegionId
                    : configProvider.Region.RegionId;
            }

            var sdkClient = new ObjectStorageClient(provider, new ClientConfiguration());
            string @namespace;
            try
            {
                @namespace = string.IsNullOrEmpty(options.Namespace)
                    ? (await sdkClient.GetNamespace(new GetNamespaceRequest(), retryConfiguration: null, cancellationToken: cancellationToken).ConfigureAwait(false)).Value
                    : options.Namespace;
            }
            catch
            {
                sdkClient.Dispose();
                throw;
            }

            var realClient = new RealOciClient(sdkClient, @namespace, options.BucketName, regionId, ownsClient: true);
            return await BuildAsync(realClient, options.RootPath, options.PathMode, cancellationToken).ConfigureAwait(false);
        }

        // === Legacy factories: thin wrappers over Create. Will be removed in v1. ===

        /// <summary>
        /// Build a FileHub using an OCI config file (<c>~/.oci/config</c>) and profile.
        /// </summary>
        [Obsolete("Use Create(OciHubOptions.FromConfigFile(bucketName, profile, configFilePath, rootPath)) instead. This overload will be removed in v1.")]
        public static OracleObjectStorageFileHub FromConfigFile(
            string rootPath,
            string bucketName,
            string configFilePath = null,
            string profile = "DEFAULT",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                ConfigFilePath = configFilePath,
                Profile = profile,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(OciHubOptions.FromConfigFile(bucketName, profile, configFilePath, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<OracleObjectStorageFileHub> FromConfigFileAsync(
            string rootPath,
            string bucketName,
            string configFilePath = null,
            string profile = "DEFAULT",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                ConfigFilePath = configFilePath,
                Profile = profile,
                PathMode = pathMode,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub from a user-supplied authentication provider and region id.
        /// </summary>
        [Obsolete("Use Create(OciHubOptions.FromProvider(bucketName, provider, regionId, rootPath)) instead. This overload will be removed in v1.")]
        public static OracleObjectStorageFileHub FromProvider(
            string rootPath,
            string bucketName,
            IAuthenticationDetailsProvider provider,
            string regionId,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Provider = provider,
                RegionId = regionId,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(OciHubOptions.FromProvider(bucketName, provider, regionId, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<OracleObjectStorageFileHub> FromProviderAsync(
            string rootPath,
            string bucketName,
            IAuthenticationDetailsProvider provider,
            string regionId,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Provider = provider,
                RegionId = regionId,
                PathMode = pathMode,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub from a <see cref="ConfigFileAuthenticationDetailsProvider"/>;
        /// region is read from the provider.
        /// </summary>
        [Obsolete("Use Create(OciHubOptions.FromProvider(bucketName, ConfigFileAuthenticationDetailsProvider, rootPath)) — region is read from provider.Region.RegionId. This overload will be removed in v1.")]
        public static OracleObjectStorageFileHub FromProvider(
            string rootPath,
            string bucketName,
            ConfigFileAuthenticationDetailsProvider provider,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Provider = provider,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(OciHubOptions.FromProvider(bucketName, ConfigFileAuthenticationDetailsProvider, rootPath), ct) — region is read from provider.Region.RegionId. This overload will be removed in v1.")]
        public static Task<OracleObjectStorageFileHub> FromProviderAsync(
            string rootPath,
            string bucketName,
            ConfigFileAuthenticationDetailsProvider provider,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Provider = provider,
                PathMode = pathMode,
            }, cancellationToken);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="ObjectStorageClient"/>.
        /// </summary>
        [Obsolete("Use Create(OciHubOptions.FromClient(bucketName, client, regionId, namespace, rootPath)) instead. This overload will be removed in v1.")]
        public static OracleObjectStorageFileHub FromClient(
            string bucketName,
            string rootPath,
            ObjectStorageClient client,
            string regionId,
            string @namespace,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => Create(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                RegionId = regionId,
                Namespace = @namespace,
                PathMode = pathMode,
            });

        [Obsolete("Use CreateAsync(OciHubOptions.FromClient(bucketName, client, regionId, namespace, rootPath), ct) instead. This overload will be removed in v1.")]
        public static Task<OracleObjectStorageFileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            ObjectStorageClient client,
            string regionId,
            string @namespace,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => CreateAsync(new OciHubOptions
            {
                BucketName = bucketName,
                RootPath = rootPath,
                Client = client,
                RegionId = regionId,
                Namespace = @namespace,
                PathMode = pathMode,
            }, cancellationToken);

        // === Internal factories (used by tests with an in-memory fake) ===

        /// <summary>
        /// Internal factory — accepts any <see cref="IOciClient"/> implementation.
        /// Used by tests with an in-memory fake so the driver logic can be
        /// exercised end-to-end with no network I/O.
        /// </summary>
        internal static OracleObjectStorageFileHub FromOciClient(
            IOciClient client,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => SyncBridge.Run(ct => FromOciClientAsync(client, rootPath, pathMode, ct));

        internal static Task<OracleObjectStorageFileHub> FromOciClientAsync(
            IOciClient client,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return BuildAsync(client, rootPath, pathMode, cancellationToken);
        }

        private static async Task<OracleObjectStorageFileHub> BuildAsync(
            IOciClient client,
            string rootPath,
            DirectoryPathMode pathMode,
            CancellationToken cancellationToken)
        {
            var hub = new OracleObjectStorageFileHub(new OciSession(client), rootPath, pathMode);
            var normalized = OciPathUtil.NormalizePrefix(rootPath);
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
