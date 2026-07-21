namespace FileHub.OracleObjectStorage.Tests.Integration;

/// <summary>
/// Opt-in integration coverage for the stream-based multipart path against
/// real OCI Object Storage. Signed multipart parts are not included because
/// OCI PARs do not expose the S3-style per-part signing contract.
/// </summary>
public class RealOciMultipartIntegrationTests : RealIntegrationTestBase
{
    private const int PartSize = 5 * 1024 * 1024;

    [RequiresOci]
    public async Task MultipartStream_LargeFile_RoundTripAsync()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "multipart-stream");
        var name = $"multipart-{Guid.NewGuid():N}.bin";

        var payload = new byte[PartSize + 1024 * 1024];
        new Random(1).NextBytes(payload);

        try
        {
            var file = rootDir.CreateFile(name);
            using (var stream = await file.GetWriteStreamAsync(
                new OciWriteOptions { Multipart = new MultipartStreamOptions(PartSize, PartSize) },
                WriteStreamPreference.Multipart))
            {
                await stream.WriteAsync(payload, 0, payload.Length);
            }

            var downloaded = rootDir.OpenFile(name).ReadAllBytes();
            Assert.Equal(payload.Length, downloaded.Length);
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            rootDir.DeleteIfExists(name);
            rootDir.Delete();
        }
    }
}
