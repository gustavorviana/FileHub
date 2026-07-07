using Oci.Common.Auth;
using Oci.Common.Retry;
using Oci.ObjectstorageService;

namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Construction options for <see cref="OracleObjectStorageFileHub.Create(OciHubOptions)"/>.
    /// Set exactly one auth strategy: <see cref="Client"/>, <see cref="Provider"/>,
    /// or the config-file pair (<see cref="ConfigFilePath"/> / <see cref="Profile"/>).
    /// When all three are null, defaults to <c>~/.oci/config</c> with the <c>DEFAULT</c> profile.
    /// <para>
    /// Prefer the typed <c>From*</c> static factories on this class — each one
    /// captures a valid combination of options and prevents accidentally
    /// mixing mutually-exclusive auth strategies:
    /// </para>
    /// <code>
    /// var hub = OracleObjectStorageFileHub.Create(OciHubOptions.FromConfigFile("reports", profile: "prod"));
    /// </code>
    /// The object initializer pattern (<c>new OciHubOptions { ... }</c>) stays
    /// available for advanced scenarios where you need to populate fields the
    /// typed factories don't expose.
    /// </summary>
    public sealed class OciHubOptions
    {
        /// <summary>Target bucket. Required.</summary>
        public string BucketName { get; init; }

        /// <summary>Prefix inside the bucket that the hub treats as its root. Defaults to <c>""</c> (whole bucket).</summary>
        public string RootPath { get; init; } = "";


        /// <summary>OCI region id (e.g. <c>"sa-saopaulo-1"</c>). Required with <see cref="Client"/>; optional with <see cref="Provider"/> (falls back to <c>provider.Region.RegionId</c>); optional with the config-file strategy (falls back to profile's region).</summary>
        public string RegionId { get; init; }

        /// <summary>Tenancy namespace. Required with <see cref="Client"/>; resolved automatically via <c>GetNamespace</c> for the other strategies when omitted.</summary>
        public string Namespace { get; init; }

        /// <summary>Path to an OCI config file. Mutually exclusive with <see cref="Provider"/> and <see cref="Client"/>. Defaults to <c>~/.oci/config</c>.</summary>
        public string ConfigFilePath { get; init; }

        /// <summary>OCI config profile name. Mutually exclusive with <see cref="Provider"/> and <see cref="Client"/>. Defaults to <c>"DEFAULT"</c>.</summary>
        public string Profile { get; init; }

        /// <summary>Explicit authentication provider (instance principals, resource principals, custom). Mutually exclusive with the config-file pair and <see cref="Client"/>. The hub creates and owns an <see cref="ObjectStorageClient"/> built from this provider.</summary>
        public IAuthenticationDetailsProvider Provider { get; init; }

        /// <summary>Externally-owned SDK client. Mutually exclusive with <see cref="Provider"/> and the config-file pair. Caller keeps ownership; hub disposal is a no-op on it.</summary>
        public ObjectStorageClient Client { get; init; }

        /// <summary>
        /// Retry strategy for the SDK client the hub creates — i.e. with the
        /// config-file pair or <see cref="Provider"/>. When null, the OCI SDK
        /// default applies: no automatic retries. Pass
        /// <c>RetryConfiguration.DefaultRetryConfiguration</c> to enable the
        /// SDK's standard backoff on 429/5xx. Mutually exclusive with
        /// <see cref="Client"/> — an external client already carries its own
        /// configuration.
        /// </summary>
        public RetryConfiguration RetryConfiguration { get; init; }

        // === Typed factories: one per valid auth strategy ===

        /// <summary>
        /// Options for the OCI config file at <c>~/.oci/config</c> (or
        /// <paramref name="configFilePath"/> when provided), using the named
        /// <paramref name="profile"/>. Region is read from the profile; namespace
        /// is resolved automatically via <c>GetNamespace</c>. Hub creates and
        /// owns the SDK client.
        /// </summary>
        public static OciHubOptions FromConfigFile(
            string bucketName,
            string profile = "DEFAULT",
            string configFilePath = null,
            string rootPath = "")
            => new OciHubOptions
            {
                BucketName = bucketName,
                Profile = profile,
                ConfigFilePath = configFilePath,
                RootPath = rootPath,
            };

        /// <summary>
        /// Options for a generic <see cref="IAuthenticationDetailsProvider"/>
        /// (instance principals, resource principals, custom). <paramref name="regionId"/>
        /// is required because <see cref="IAuthenticationDetailsProvider"/> does
        /// not expose a region. Namespace is resolved automatically.
        /// Hub creates and owns the SDK client.
        /// </summary>
        public static OciHubOptions FromProvider(
            string bucketName,
            IAuthenticationDetailsProvider provider,
            string regionId,
            string rootPath = "")
            => new OciHubOptions
            {
                BucketName = bucketName,
                Provider = provider,
                RegionId = regionId,
                RootPath = rootPath,
            };

        /// <summary>
        /// Options for a <see cref="ConfigFileAuthenticationDetailsProvider"/> —
        /// region is read from <c>provider.Region.RegionId</c>, so callers don't
        /// repeat it. Namespace is resolved automatically.
        /// </summary>
        public static OciHubOptions FromProvider(
            string bucketName,
            ConfigFileAuthenticationDetailsProvider provider,
            string rootPath = "")
            => new OciHubOptions
            {
                BucketName = bucketName,
                Provider = provider,
                RootPath = rootPath,
            };

        /// <summary>
        /// Options for an externally-owned <see cref="ObjectStorageClient"/>.
        /// <paramref name="regionId"/> and <paramref name="namespace"/> are both
        /// required because no auto-resolution path is available. Caller retains
        /// client ownership; hub disposal is a no-op on it.
        /// </summary>
        public static OciHubOptions FromClient(
            string bucketName,
            ObjectStorageClient client,
            string regionId,
            string @namespace,
            string rootPath = "")
            => new OciHubOptions
            {
                BucketName = bucketName,
                Client = client,
                RegionId = regionId,
                Namespace = @namespace,
                RootPath = rootPath,
            };
    }
}
