using FileHub.OracleObjectStorage.Internal;
using Oci.Common;
using Oci.Common.Auth;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Requests;
using System;
using System.Threading;
using System.Threading.Tasks;

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

        public DirectoryEntry Root { get; }

        private OracleObjectStorageFileHub(OciSession session, string rootPath)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var rootPrefix = PathUtil.NormalizePrefix(rootPath);
            Root = new OracleObjectStorageDirectory(_session, rootPrefix);
        }

        // === Canonical factory: options bag ===

        /// <summary>
        /// Build a FileHub from an <see cref="OracleObjectStorageHubOptions"/> bag. This is the
        /// only entry point: covers config-file, provider-, and client-based
        /// construction in one place. Pick the strategy via a typed factory on
        /// <see cref="OracleObjectStorageHubOptions"/> (<c>FromConfigFile</c>, <c>FromProvider</c>,
        /// <c>FromClient</c>).
        /// </summary>
        public static OracleObjectStorageFileHub Create(OracleObjectStorageHubOptions options)
            => SyncBridge.Run(ct => CreateAsync(options, ct));

        /// <inheritdoc cref="Create(OracleObjectStorageHubOptions)"/>
        public static async Task<OracleObjectStorageFileHub> CreateAsync(
            OracleObjectStorageHubOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.BucketName))
                throw new ArgumentException("BucketName cannot be null or empty.", nameof(options));
            ValidateMultipartOptions(options.Multipart, nameof(options));

            var strategies = (options.Client != null ? 1 : 0)
                           + (options.Provider != null ? 1 : 0)
                           + (options.ConfigFilePath != null || options.Profile != null ? 1 : 0);
            // The "config file" strategy is also the default when nothing is set.

            if (strategies > 1)
                throw new ArgumentException("OracleObjectStorageHubOptions accepts exactly one of Client, Provider, or (ConfigFilePath/Profile).", nameof(options));

            if (options.Client != null && options.RetryConfiguration != null)
                throw new ArgumentException("RetryConfiguration only applies when the hub creates the client; an external Client already carries its own configuration.", nameof(options));

            if (options.Client != null)
            {
                if (string.IsNullOrEmpty(options.RegionId))
                    throw new ArgumentException("RegionId is required when Client is provided.", nameof(options));
                if (string.IsNullOrEmpty(options.Namespace))
                    throw new ArgumentException("Namespace is required when Client is provided.", nameof(options));
                var real = new RealOciClient(options.Client, options.Namespace, options.BucketName, options.RegionId, ownsClient: false);
                return await BuildAsync(real, options.RootPath, cancellationToken, options.Multipart).ConfigureAwait(false);
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

            // Retry comes from the consumer via options — the hub pins nothing.
            // Null means the OCI SDK default: no automatic retries, every
            // 429/5xx surfaces immediately. Per-call retryConfiguration stays
            // null in RealOciClient, which falls back to this client-level one.
            var sdkClient = new ObjectStorageClient(provider, new ClientConfiguration
            {
                RetryConfiguration = options.RetryConfiguration
            });
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
            return await BuildAsync(realClient, options.RootPath, cancellationToken, options.Multipart).ConfigureAwait(false);
        }

        // === Internal factories (used by tests with an in-memory fake) ===

        /// <summary>
        /// Internal factory — accepts any <see cref="IOciClient"/> implementation.
        /// Used by tests with an in-memory fake so the driver logic can be
        /// exercised end-to-end with no network I/O.
        /// </summary>
        internal static OracleObjectStorageFileHub FromOciClient(
            IOciClient client,
            string rootPath = "")
            => SyncBridge.Run(ct => FromOciClientAsync(client, rootPath, ct));

        internal static Task<OracleObjectStorageFileHub> FromOciClientAsync(
            IOciClient client,
            string rootPath = "",
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return BuildAsync(client, rootPath, cancellationToken);
        }

        private static async Task<OracleObjectStorageFileHub> BuildAsync(
            IOciClient client,
            string rootPath,
            CancellationToken cancellationToken,
            MultipartStreamOptions multipart = null)
        {
            multipart ??= MultipartStreamOptions.Default;
            ValidateMultipartOptions(multipart, nameof(multipart));
            var hub = new OracleObjectStorageFileHub(new OciSession(client, multipart), rootPath);
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

            if (multipart.PartSize <= 0 || multipart.PartSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, $"MultipartPartSize must be between 1 and {int.MaxValue} bytes for the in-memory stream implementation.");

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
