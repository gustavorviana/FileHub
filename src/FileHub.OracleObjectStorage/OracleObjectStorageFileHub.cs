using System;
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

        private OracleObjectStorageFileHub(OciSession session, string rootPath)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            var rootPrefix = OciPathUtil.NormalizePrefix(rootPath);
            Root = new OracleObjectStorageDirectory(_session, rootPrefix);
        }

        /// <summary>
        /// Build a FileHub using an OCI config file (<c>~/.oci/config</c>) and profile.
        /// </summary>
        public static OracleObjectStorageFileHub FromConfigFile(
            string rootPath,
            string bucketName,
            string configFilePath = null,
            string profile = "DEFAULT")
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));

            var provider = string.IsNullOrEmpty(configFilePath)
                ? new ConfigFileAuthenticationDetailsProvider(profile ?? "DEFAULT")
                : new ConfigFileAuthenticationDetailsProvider(configFilePath, profile ?? "DEFAULT");

            return FromProvider(rootPath, bucketName, provider, provider.Region.RegionId);
        }

        /// <summary>
        /// Build a FileHub from a user-supplied authentication provider and region id.
        /// </summary>
        public static OracleObjectStorageFileHub FromProvider(
            string rootPath,
            string bucketName,
            IAuthenticationDetailsProvider provider,
            string regionId)
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
                @namespace = sdkClient.GetNamespace(new GetNamespaceRequest()).GetAwaiter().GetResult().Value;
            }
            catch
            {
                sdkClient.Dispose();
                throw;
            }

            var real = new RealOciClient(sdkClient, @namespace, bucketName, regionId, ownsClient: true);
            return FromOciClient(real, rootPath);
        }


        /// <summary>
        /// Build a FileHub from a user-supplied authentication provider and region id.
        /// </summary>
        public static OracleObjectStorageFileHub FromProvider(
            string rootPath,
            string bucketName,
            ConfigFileAuthenticationDetailsProvider provider)
            => FromProvider(rootPath, bucketName, provider, provider.Region.RegionId);

        /// <summary>
        /// Build a FileHub around an externally-owned <see cref="ObjectStorageClient"/>.
        /// The caller retains ownership of the client — disposing this FileHub does
        /// <b>not</b> dispose it.
        /// </summary>
        public static OracleObjectStorageFileHub FromClient(
            string bucketName,
            string rootPath,
            ObjectStorageClient client,
            string regionId,
            string @namespace)
        {
            if (string.IsNullOrEmpty(bucketName))
                throw new ArgumentException("Bucket cannot be null or empty.", nameof(bucketName));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(regionId))
                throw new ArgumentException("Region id required.", nameof(regionId));
            if (string.IsNullOrEmpty(@namespace))
                throw new ArgumentException("Namespace cannot be null or empty.", nameof(@namespace));

            var real = new RealOciClient(client, @namespace, bucketName, regionId, ownsClient: false);
            return FromOciClient(real, rootPath);
        }

        /// <summary>
        /// Internal factory — accepts any <see cref="IOciClient"/> implementation.
        /// Used by tests with an in-memory fake so the driver logic can be
        /// exercised end-to-end with no network I/O.
        /// </summary>
        internal static OracleObjectStorageFileHub FromOciClient(IOciClient client, string rootPath = "")
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            var session = new OciSession(client);
            return new OracleObjectStorageFileHub(session, rootPath);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}
