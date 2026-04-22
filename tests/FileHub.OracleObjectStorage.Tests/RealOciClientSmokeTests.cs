using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// Minimal smoke tests that actually hit OCI. They validate that
/// <c>RealOciClient</c> behaves the same as <c>InMemoryOciClient</c> on the
/// happy path + key error translations (404 → FileNotFoundException).
/// Skipped when OCI env vars are missing.
/// </summary>
public class RealOciClientSmokeTests
{
    private static OracleObjectStorageFileHub NewHub(string subfolder)
    {
        var bucket = Environment.GetEnvironmentVariable("FILEHUB_OCI_BUCKET")!;
        var configFile = Environment.GetEnvironmentVariable("FILEHUB_OCI_CONFIG_FILE");
        var profile = Environment.GetEnvironmentVariable("FILEHUB_OCI_PROFILE") ?? "DEFAULT";
        var basePrefix = Environment.GetEnvironmentVariable("FILEHUB_OCI_TEST_PREFIX") ?? "filehub-tests/";
        if (!basePrefix.EndsWith("/")) basePrefix += "/";

        return OracleObjectStorageFileHub.FromConfigFile(
            rootPath: basePrefix + "smoke/" + subfolder + "/" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/",
            bucket,
            configFilePath: configFile,
            profile: profile);
    }

    [RequiresOci]
    public void UploadDownloadDelete_RoundTrip()
    {
        using var hub = NewHub("roundtrip");
        try
        {
            var file = hub.Root.CreateFile("smoke.txt");
            file.SetText("round-trip");

            var reopened = hub.Root.OpenFile("smoke.txt");
            Assert.Equal("round-trip", reopened.ReadAllText());
            reopened.Delete();
            Assert.False(hub.Root.ItemExists("smoke.txt"));
        }
        finally
        {
            try { hub.Root.Delete(); } catch (NotSupportedException) { }
        }
    }

    [RequiresOci]
    public void MissingObject_ThrowsFileNotFoundException()
    {
        using var hub = NewHub("notfound");
        try
        {
            Assert.False(hub.Root.TryOpenFile("does-not-exist.txt", out var _));
            Assert.Throws<FileNotFoundException>(() => hub.Root.OpenFile("does-not-exist.txt"));
        }
        finally
        {
            try { hub.Root.Delete(); } catch (NotSupportedException) { }
        }
    }

    [RequiresOci]
    public async Task GetSignedUrl_ReturnsDownloadableUrl()
    {
        using var hub = NewHub("par");
        try
        {
            var file = (OracleObjectStorageFile)hub.Root.CreateFile("signed.txt");
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
            try { hub.Root.Delete(); } catch (NotSupportedException) { }
        }
    }
}
