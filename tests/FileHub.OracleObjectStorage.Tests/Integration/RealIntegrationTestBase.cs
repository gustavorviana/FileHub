using Oci.Common.Auth;
using Oci.ObjectstorageService;

namespace FileHub.OracleObjectStorage.Tests.Integration;

public abstract class RealIntegrationTestBase : IAsyncLifetime
{
    private const string Prefix = "filehub-tests/";

    private readonly string _runId;
    private readonly string _rootDir;
    private readonly bool _autoCleanupDirs;

    protected string RegionId { get; }

    private readonly List<OracleObjectStorageFileHub> _hubs = [];
    private readonly ObjectStorageClient _client;

    public RealIntegrationTestBase(string? suffix = null, bool autoCleanupDirs = false)
    {
        _autoCleanupDirs = autoCleanupDirs;
        _runId = Guid.NewGuid().ToString("N")[..8];
        _rootDir = Prefix + (string.IsNullOrEmpty(suffix) ? "" : suffix + "/");

        var configFile = Environment.GetEnvironmentVariable("FILEHUB_OCI_CONFIG_FILE");
        var profile = Environment.GetEnvironmentVariable("FILEHUB_OCI_PROFILE") ?? "DEFAULT";

        var provider = string.IsNullOrEmpty(configFile)
            ? new ConfigFileAuthenticationDetailsProvider(profile)
            : new ConfigFileAuthenticationDetailsProvider(configFile, profile);

        RegionId = provider.Region.RegionId;
        _client = new ObjectStorageClient(provider);
    }

    protected async Task<OracleObjectStorageDirectory> GetRootDir(BucketName bucket)
    {
        var hub = await OracleObjectStorageFileHub.CreateAsync(CreateOptions(bucket));
        _hubs.Add(hub);
        return (OracleObjectStorageDirectory)await hub.Root.OpenDirectoryAsync(_runId, true);
    }

    private OciHubOptions CreateOptions(BucketName bucket)
    {
        var bucketName = GetBucketName(bucket);
        var ns = Environment.GetEnvironmentVariable("FILEHUB_OCI_NAMESPACE")!;

        return OciHubOptions.FromClient(bucketName, _client, RegionId, ns, _rootDir);
    }

    protected static string GetBucketName(BucketName bucket)
    {
        return bucket switch
        {
            BucketName.A => Environment.GetEnvironmentVariable("FILEHUB_OCI_BUCKET")!,
            BucketName.B => Environment.GetEnvironmentVariable("FILEHUB_OCI_BUCKET_B")!,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };
    }

    public virtual async Task InitializeAsync()
    {

    }

    public virtual async Task DisposeAsync()
    {
        foreach (var hub in _hubs)
        {
            try
            {
                if (_autoCleanupDirs)
                    await hub.Root.DeleteAsync();

                hub.Dispose();
            }
            catch
            {
            }
        }
        _client.Dispose();
    }

    protected enum BucketName
    {
        A,
        B
    }
}
