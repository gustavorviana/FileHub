namespace FileHub.OracleObjectStorage.Tests.Integration;

/// <summary>
/// Minimal integration tests that actually hit OCI. They validate that
/// <c>RealOciClient</c> behaves the same as <c>InMemoryOciClient</c> on the
/// happy path + key error translations (404 → FileNotFoundException).
/// Skipped when OCI env vars are missing.
/// </summary>
public class RealOciClientIntegrationTests : RealIntegrationTestBase
{
    [RequiresOci]
    public async Task UploadDownloadDelete_RoundTripAsync()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "roundtrip");
        try
        {
            var file = rootDir.CreateFile("integration.txt");
            file.SetText("round-trip");

            var reopened = rootDir.OpenFile("integration.txt");
            Assert.Equal("round-trip", reopened.ReadAllText());
            reopened.Delete();
            Assert.False(rootDir.FileExists("integration.txt"));
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresOci]
    public async Task MissingObject_ThrowsFileNotFoundExceptionAsync()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "notfound");
        try
        {
            Assert.False(rootDir.TryOpenFile("does-not-exist.txt", out var _));
            Assert.Throws<FileNotFoundException>(() => rootDir.OpenFile("does-not-exist.txt"));
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresOci]
    public async Task GetSignedUrl_ReturnsDownloadableUrlAsync()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "par");
        try
        {
            var file = (OracleObjectStorageFile)rootDir.CreateFile("signed.txt");
            file.SetText("signed-content");

            var url = await file.GetSignedUrlAsync(TimeSpan.FromMinutes(2));

            using var http = new HttpClient();
            using var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Equal("signed-content", body);
        }
        finally
        {
            rootDir.Delete();
        }
    }

    [RequiresOci]
    public async Task GetSignedUploadUrl_PutsObjectIntoBucketAsync()
    {
        var rootDir = await GetRootDirAsync(BucketName.A, "upload-par");
        try
        {
            var url = await ((ISignedUploadable)rootDir).GetSignedUploadUrlAsync(
                "uploaded.txt", TimeSpan.FromMinutes(2));

            using var http = new HttpClient();
            using var content = new StringContent("uploaded-through-par");
            using var response = await http.PutAsync(url, content);
            response.EnsureSuccessStatusCode();

            Assert.Equal("uploaded-through-par", rootDir.OpenFile("uploaded.txt").ReadAllText());
        }
        finally
        {
            rootDir.Delete();
        }
    }
}
