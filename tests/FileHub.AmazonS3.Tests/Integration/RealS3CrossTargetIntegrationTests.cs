using System;
using System.Text;

namespace FileHub.AmazonS3.Tests.Integration;

/// <summary>
/// Opt-in integration tests against real AWS S3 covering cross-bucket and
/// cross-region copy paths. Requires:
/// <list type="bullet">
///   <item>AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_REGION, FILEHUB_S3_BUCKET</item>
///   <item>FILEHUB_S3_BUCKET_B, AWS_REGION_B (second bucket)</item>
/// </list>
/// The cross-region test only runs when AWS_REGION_B != AWS_REGION; the
/// cross-bucket test runs regardless.
/// </summary>
public class RealS3CrossTargetIntegrationTests : RealS3IntegrationTestBase
{
    [RequiresAwsSecondBucket]
    public async Task CrossBucket_CopyTo_ServerSide()
    {
        var rootA = await GetRootDirAsync(BucketName.A, "cross-bucket");
        var rootB = await GetRootDirAsync(BucketName.B, "cross-bucket");

        var payload = Encoding.UTF8.GetBytes("cross-bucket server-side");
        rootA.CreateFile("copy.txt").SetBytes(payload);

        try
        {
            rootA.OpenFile("copy.txt").CopyTo(rootB, "copy.txt");

            var downloaded = rootB.OpenFile("copy.txt").ReadAllBytes();
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            TryDelete(rootA.Delete);
            TryDelete(rootB.Delete);
        }
    }

    [RequiresAwsCrossRegion]
    public async Task CrossRegion_CopyTo_ServerSide()
    {
        var rootA = await GetRootDirAsync(BucketName.A, "cross-region");
        var rootB = await GetRootDirAsync(BucketName.B, "cross-region");

        var payload = Encoding.UTF8.GetBytes($"cross-region @ {DateTime.UtcNow:O}");
        rootA.CreateFile("copy.txt").SetBytes(payload);

        try
        {
            // The key invariant: this copy is issued through the destination
            // client (region B), which is the only endpoint that routes a
            // cross-region CopyObject correctly. If the implementation ever
            // regresses to source-client, S3 will reject here.
            rootA.OpenFile("copy.txt").CopyTo(rootB, "copy.txt");

            var downloaded = rootB.OpenFile("copy.txt").ReadAllBytes();
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            TryDelete(rootA.Delete);
            TryDelete(rootB.Delete);
        }
    }

    private static void TryDelete(Action action)
    {
        try { action(); } catch { /* cleanup best-effort */ }
    }
}
