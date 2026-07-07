using Amazon.Runtime;
using Amazon.S3;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// Construction options for <see cref="AmazonS3FileHub.Create(S3HubOptions)"/>.
    /// Set exactly one of <see cref="Client"/>, <see cref="Credentials"/>, or
    /// <see cref="Profile"/> to pick the auth strategy. Region is required when
    /// the chosen strategy does not carry one.
    /// <para>
    /// Prefer the typed <c>From*</c> static factories on this class — each one
    /// captures a valid combination of options and prevents accidentally
    /// mixing mutually-exclusive auth strategies:
    /// </para>
    /// <code>
    /// var hub = AmazonS3FileHub.Create(S3HubOptions.FromCredentials("reports", creds, "us-east-1"));
    /// </code>
    /// The object initializer pattern (<c>new S3HubOptions { ... }</c>) stays
    /// available for advanced scenarios where you need to populate fields the
    /// typed factories don't expose.
    /// </summary>
    public sealed class S3HubOptions
    {
        /// <summary>Target bucket. Required.</summary>
        public string BucketName { get; init; }

        /// <summary>Prefix inside the bucket that the hub treats as its root. Defaults to <c>""</c> (whole bucket).</summary>
        public string RootPath { get; init; } = "";

        /// <summary>How nested-path operations descend. Defaults to <see cref="DirectoryPathMode.Direct"/> — cost-optimised for S3.</summary>
        public DirectoryPathMode PathMode { get; init; } = DirectoryPathMode.Direct;

        /// <summary>AWS profile name from <c>~/.aws/credentials</c>. Mutually exclusive with <see cref="Credentials"/> and <see cref="Client"/>. When all three are null, defaults to <c>"default"</c>.</summary>
        public string Profile { get; init; }

        /// <summary>AWS region (e.g. <c>"us-east-1"</c>). Required when not derivable from the profile or an <see cref="AmazonS3Client"/>.</summary>
        public string Region { get; init; }

        /// <summary>Explicit credentials. Mutually exclusive with <see cref="Profile"/> and <see cref="Client"/>. The hub creates and owns an <see cref="AmazonS3Client"/> built from these.</summary>
        public AWSCredentials Credentials { get; init; }

        /// <summary>Externally-owned SDK client. Mutually exclusive with <see cref="Profile"/> and <see cref="Credentials"/>. Caller keeps ownership; hub disposal is a no-op on it.</summary>
        public IAmazonS3 Client { get; init; }

        /// <summary>
        /// SDK configuration (retry mode, timeouts, proxy, ...) for the client
        /// the hub creates — i.e. with <see cref="Credentials"/> or
        /// <see cref="Profile"/>. When null, plain SDK defaults apply; the hub
        /// pins nothing. The resolved <see cref="Region"/> takes precedence
        /// over any <c>RegionEndpoint</c> set here; leave <see cref="Region"/>
        /// null to use this config's <c>RegionEndpoint</c>. Mutually exclusive
        /// with <see cref="Client"/> — an external client already carries its
        /// own configuration.
        /// </summary>
        public AmazonS3Config SdkConfig { get; init; }

        // === Typed factories: one per valid auth strategy ===

        /// <summary>
        /// Options for the named AWS profile from <c>~/.aws/credentials</c>.
        /// <paramref name="region"/> overrides the profile's region when set;
        /// otherwise the profile must carry one. Hub creates and owns the SDK client.
        /// </summary>
        public static S3HubOptions FromProfile(
            string bucketName,
            string profile = "default",
            string region = null,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => new S3HubOptions
            {
                BucketName = bucketName,
                Profile = profile,
                Region = region,
                RootPath = rootPath,
                PathMode = pathMode,
            };

        /// <summary>
        /// Options for explicit AWS credentials and region. Hub creates and owns the SDK client.
        /// </summary>
        public static S3HubOptions FromCredentials(
            string bucketName,
            AWSCredentials credentials,
            string region,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => new S3HubOptions
            {
                BucketName = bucketName,
                Credentials = credentials,
                Region = region,
                RootPath = rootPath,
                PathMode = pathMode,
            };

        /// <summary>
        /// Options for an externally-owned <see cref="IAmazonS3"/>. <paramref name="region"/>
        /// is required because <see cref="IAmazonS3"/> does not expose the region.
        /// Caller retains client ownership; hub disposal is a no-op on it.
        /// </summary>
        public static S3HubOptions FromClient(
            string bucketName,
            IAmazonS3 client,
            string region,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => new S3HubOptions
            {
                BucketName = bucketName,
                Client = client,
                Region = region,
                RootPath = rootPath,
                PathMode = pathMode,
            };

        /// <summary>
        /// Options for an externally-owned <see cref="AmazonS3Client"/>. The
        /// region is read from <c>client.Config.RegionEndpoint</c>, so callers
        /// don't repeat it. Caller retains client ownership.
        /// </summary>
        public static S3HubOptions FromClient(
            string bucketName,
            AmazonS3Client client,
            string rootPath = "",
            DirectoryPathMode pathMode = DirectoryPathMode.Direct)
            => new S3HubOptions
            {
                BucketName = bucketName,
                Client = client,
                RootPath = rootPath,
                PathMode = pathMode,
            };
    }
}
