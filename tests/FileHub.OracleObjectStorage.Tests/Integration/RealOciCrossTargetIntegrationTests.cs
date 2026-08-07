using System.Text;

namespace FileHub.OracleObjectStorage.Tests.Integration;

/// <summary>
/// Opt-in integration tests against real OCI Object Storage covering the
/// cross-bucket server-side copy path. Requires:
/// <list type="bullet">
///   <item>FILEHUB_OCI_CONFIG_FILE, FILEHUB_OCI_PROFILE, FILEHUB_OCI_BUCKET, FILEHUB_OCI_NAMESPACE</item>
///   <item>FILEHUB_OCI_BUCKET_B — a second bucket in the SAME tenancy/namespace/region</item>
/// </list>
/// Both hubs are built from a <b>single</b> <see cref="Oci.ObjectstorageService.ObjectStorageClient"/>
/// so they share a credential scope: that is what routes the copy through OCI's
/// server-side <c>CopyObject</c> instead of streaming it over the wire. Because
/// one client is pinned to one region endpoint, the second bucket must be in
/// the same region — cross-region copy cannot be verified through this path.
/// </summary>
public class RealOciCrossTargetIntegrationTests : RealIntegrationTestBase
{
    private const string SubFolder = "cross-target";

    [RequiresOciSecondBucket]
    public async Task CrossBucket_CopyTo_ServerSideAsync()
    {
        var rootDirA = await GetRootDirAsync(BucketName.A, SubFolder);
        var rootDirB = await GetRootDirAsync(BucketName.B, SubFolder);

        var name = $"cross-bucket-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("cross-bucket server-side");
        await rootDirA.CreateFileAsync(name, payload);

        try
        {
            await (await rootDirA.OpenFileAsync(name)).CopyToAsync(rootDirB, name);

            var downloaded = await (await rootDirA.OpenFileAsync(name)).ReadAllBytesAsync();
            Assert.Equal(payload, downloaded);

            // Source is untouched by a copy.
            Assert.True(await rootDirA.FileExistsAsync(name));
        }
        finally
        {
            await TryDelete(async () => (await rootDirA.OpenFileAsync(name)).Delete());
            await TryDelete(async () => (await rootDirB.OpenFileAsync(name)).Delete());
        }
    }

    [RequiresOciSecondBucket]
    public async Task CrossBucket_MoveTo_DeletesSourceAsync()
    {
        var rootDirA = await GetRootDirAsync(BucketName.A, SubFolder);
        var rootDirB = await GetRootDirAsync(BucketName.B, SubFolder);

        var name = $"cross-bucket-move-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("moving across buckets");
        rootDirA.CreateFile(name, payload);

        try
        {
            rootDirA.OpenFile(name).MoveTo(rootDirB, name);

            Assert.Equal(payload, (await rootDirB.OpenFileAsync(name)).ReadAllBytes());
            Assert.False(await rootDirA.FileExistsAsync(name));
        }
        finally
        {
            await TryDelete(async () => (await rootDirA.OpenFileAsync(name)).Delete());
            await TryDelete(async () => (await rootDirB.OpenFileAsync(name)).Delete());
        }
    }

    private static async Task TryDelete(Func<Task> action)
    {
        try { await action(); } catch { /* cleanup best-effort */ }
    }
}
