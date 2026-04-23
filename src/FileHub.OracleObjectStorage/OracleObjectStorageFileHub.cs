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

        private OracleObjectStorageFileHub(OciSession session, string rootPath, DirectoryPathMode pathMode)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var rootPrefix = OciPathUtil.NormalizePrefix(rootPath);
            Root = new OracleObjectStorageDirectory(_session, rootPrefix, pathMode);
        }

        // === Public factories: sync delegates to async at the top-level boundary ===

        /// <summary>
        /// Build a FileHub using an OCI config file (<c>~/.oci/config</c>) and profile.
        /// Defaults to <see cref="DirectoryPathMode.Direct"/> — cost-optimised for
        /// cloud object storage where every API call is billed. Blocks the
        /// calling thread while the root marker is created; prefer
        /// <see cref="FromConfigFileAsync"/> under a <c>SynchronizationContext</c>.
        /// </summary>
        public static OracleObjectStorageFileHub FromConfigFile(
            string rootPath,
            string bucketName,
            string configFilePath = null,
            string profile = "DEFAULT",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => FromConfigFileAsync(rootPath, bucketName, configFilePath, profile, pathMode).GetAwaiter().GetResult();

        public static async Task<OracleObjectStorageFileHub> FromConfigFileAsync(
            string rootPath,
            string bucketName,
            string configFilePath = null,
            string profile = "DEFAULT",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));

            var provider = string.IsNullOrEmpty(configFilePath)
                ? new ConfigFileAuthenticationDetailsProvider(profile ?? "DEFAULT")
                : new ConfigFileAuthenticationDetailsProvider(configFilePath, profile ?? "DEFAULT");

            return await FromProviderAsync(rootPath, bucketName, provider, provider.Region.RegionId, pathMode, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Build a FileHub from a user-supplied authentication provider and region id.
        /// Defaults to <see cref="DirectoryPathMode.Direct"/>.
        /// </summary>
        public static OracleObjectStorageFileHub FromProvider(
            string rootPath,
            string bucketName,
            IAuthenticationDetailsProvider provider,
            string regionId,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => FromProviderAsync(rootPath, bucketName, provider, regionId, pathMode).GetAwaiter().GetResult();

        public static async Task<OracleObjectStorageFileHub> FromProviderAsync(
            string rootPath,
            string bucketName,
            IAuthenticationDetailsProvider provider,
            string regionId,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrEmpty(regionId))
                throw new ArgumentException("Region id required.", nameof(regionId));

            var sdkClient = new ObjectStorageClient(provider, new ClientConfiguration());
            string @namespace;
            try
            {
                @namespace = (await sdkClient.GetNamespace(new GetNamespaceRequest(), retryConfiguration: null, cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            }
            catch
            {
                sdkClient.Dispose();
                throw;
            }

            var real = new RealOciClient(sdkClient, @namespace, bucketName, regionId, ownsClient: true);
            return await BuildAsync(real, rootPath, pathMode, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Build a FileHub from a user-supplied authentication provider and region id.
        /// Defaults to <see cref="DirectoryPathMode.Direct"/>.
        /// </summary>
        public static OracleObjectStorageFileHub FromProvider(
            string rootPath,
            string bucketName,
            ConfigFileAuthenticationDetailsProvider provider,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => FromProvider(rootPath, bucketName, provider, provider.Region.RegionId, pathMode);

        public static Task<OracleObjectStorageFileHub> FromProviderAsync(
            string rootPath,
            string bucketName,
            ConfigFileAuthenticationDetailsProvider provider,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
            => FromProviderAsync(rootPath, bucketName, provider, provider.Region.RegionId, pathMode, cancellationToken);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="ObjectStorageClient"/>.
        /// The caller retains ownership of the client — disposing this FileHub does
        /// <b>not</b> dispose it. Defaults to <see cref="DirectoryPathMode.Direct"/>.
        /// </summary>
        public static OracleObjectStorageFileHub FromClient(
            string bucketName,
            string rootPath,
            ObjectStorageClient client,
            string regionId,
            string @namespace,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => FromClientAsync(bucketName, rootPath, client, regionId, @namespace, pathMode).GetAwaiter().GetResult();

        public static Task<OracleObjectStorageFileHub> FromClientAsync(
            string bucketName,
            string rootPath,
            ObjectStorageClient client,
            string regionId,
            string @namespace,
            DirectoryPathMode pathMode = DirectoryPathMode.Direct,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(regionId))
                throw new ArgumentException("Region id required.", nameof(regionId));
            if (string.IsNullOrEmpty(@namespace))
                throw new ArgumentException("Namespace cannot be null or empty.", nameof(@namespace));

            var real = new RealOciClient(client, @namespace, bucketName, regionId, ownsClient: false);
            return BuildAsync(real, rootPath, pathMode, cancellationToken);
        }

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
            => FromOciClientAsync(client, rootPath, pathMode).GetAwaiter().GetResult();

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
