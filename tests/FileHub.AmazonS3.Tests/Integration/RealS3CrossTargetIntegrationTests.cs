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
public class RealS3CrossTargetIntegrationTests
{
    private const string Prefix = "filehub-integration";

    private static AmazonS3FileHub CreateHub(string bucket, string region)
    {
        var key = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")!;
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")!;
        var credentials = new Amazon.Runtime.BasicAWSCredentials(key, secret);
        return AmazonS3FileHub.FromCredentials(
            rootPath: Prefix,
            bucketName: bucket,
            credentials: credentials,
            region: region);
    }

    private static (AmazonS3FileHub a, AmazonS3FileHub b) CreateHubs()
    {
        var bucketA = Environment.GetEnvironmentVariable("FILEHUB_S3_BUCKET")!;
        var regionA = Environment.GetEnvironmentVariable("AWS_REGION")!;
        var bucketB = Environment.GetEnvironmentVariable("FILEHUB_S3_BUCKET_B")!;
        var regionB = Environment.GetEnvironmentVariable("AWS_REGION_B")!;
        return (CreateHub(bucketA, regionA), CreateHub(bucketB, regionB));
    }

    [RequiresAwsSecondBucket]
    public void CrossBucket_CopyTo_ServerSide()
    {
        var (hubA, hubB) = CreateHubs();
        using var _a = hubA;
        using var _b = hubB;

        var name = $"cross-bucket-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("cross-bucket server-side");
        hubA.Root.CreateFile(name).SetBytes(payload);

        try
        {
            hubA.Root.OpenFile(name).CopyTo(hubB.Root, name);

            var downloaded = hubB.Root.OpenFile(name).ReadAllBytes();
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            TryDelete(() => hubA.Root.OpenFile(name).Delete());
            TryDelete(() => hubB.Root.OpenFile(name).Delete());
        }
    }

    [RequiresAwsCrossRegion]
    public void CrossRegion_CopyTo_ServerSide()
    {
        var (hubA, hubB) = CreateHubs();
        using var _a = hubA;
        using var _b = hubB;

        var name = $"cross-region-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes($"cross-region @ {DateTime.UtcNow:O}");
        hubA.Root.CreateFile(name).SetBytes(payload);

        try
        {
            // The key invariant: this copy is issued through the destination
            // client (region B), which is the only endpoint that routes a
            // cross-region CopyObject correctly. If the implementation ever
            // regresses to source-client, S3 will reject here.
            hubA.Root.OpenFile(name).CopyTo(hubB.Root, name);

            var downloaded = hubB.Root.OpenFile(name).ReadAllBytes();
            Assert.Equal(payload, downloaded);
        }
        finally
        {
            TryDelete(() => hubA.Root.OpenFile(name).Delete());
            TryDelete(() => hubB.Root.OpenFile(name).Delete());
        }
    }

    private static void TryDelete(Action action)
    {
        try { action(); } catch { /* cleanup best-effort */ }
    }
}
