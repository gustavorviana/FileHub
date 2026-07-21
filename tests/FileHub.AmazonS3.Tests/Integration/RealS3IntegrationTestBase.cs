namespace FileHub.AmazonS3.Tests.Integration;

public abstract class RealS3IntegrationTestBase : IAsyncLifetime
{
    private const string Prefix = "filehub-tests/";

    private readonly string _runId;
    private readonly Amazon.Runtime.AWSCredentials _credentials;
    private readonly List<AmazonS3FileHub> _hubs = [];

    protected RealS3IntegrationTestBase()
    {
        _runId = Guid.NewGuid().ToString("N")[..8];

        var accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")!;
        var secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")!;
        _credentials = new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey);
    }

    protected async Task<AmazonS3Directory> GetRootDirAsync(BucketName bucket, string? subfolder = null)
    {
        var hub = await AmazonS3FileHub.CreateAsync(CreateOptions(bucket, subfolder));
        _hubs.Add(hub);
        return (AmazonS3Directory)await hub.Root.OpenDirectoryAsync(_runId, true);
    }

    private S3HubOptions CreateOptions(BucketName bucket, string? subfolder)
    {
        var bucketName = GetBucketName(bucket);
        var region = GetRegion(bucket);
        var rootDir = Prefix + (string.IsNullOrEmpty(subfolder) ? "" : subfolder + "/");

        return S3HubOptions.FromCredentials(bucketName, _credentials, region, rootDir);
    }

    protected static string GetBucketName(BucketName bucket)
    {
        return bucket switch
        {
            BucketName.A => Environment.GetEnvironmentVariable("FILEHUB_S3_BUCKET")!,
            BucketName.B => Environment.GetEnvironmentVariable("FILEHUB_S3_BUCKET_B")!,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null)
        };
    }

    protected static string GetRegion(BucketName bucket)
    {
        return bucket switch
        {
            BucketName.A => Environment.GetEnvironmentVariable("AWS_REGION")!,
            BucketName.B => Environment.GetEnvironmentVariable("AWS_REGION_B")!,
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
                hub.Dispose();
            }
            catch
            {
            }
        }
    }

    protected enum BucketName
    {
        A,
        B
    }
}
