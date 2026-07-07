using System;
using System.Text;
using Oci.Common.Auth;
using Oci.ObjectstorageService;

namespace FileHub.OracleObjectStorage.Tests.Integration;

/// <summary>
/// Opt-in integration tests against real OCI Object Storage covering the
/// cross-bucket server-side copy path. Requires:
/// <list type="bullet">
///   <item>FILEHUB_OCI_CONFIG_FILE, FILEHUB_OCI_PROFILE, FILEHUB_OCI_BUCKET, FILEHUB_OCI_NAMESPACE</item>
///   <item>FILEHUB_OCI_BUCKET_B — a second bucket in the SAME tenancy/namespace/region</item>
/// </list>
/// Both hubs are built from a <b>single</b> <see cref="ObjectStorageClient"/>
/// so they share a credential scope: that is what routes the copy through OCI's
/// server-side <c>CopyObject</c> instead of streaming it over the wire. Because
/// one client is pinned to one region endpoint, the second bucket must be in
/// the same region — cross-region copy cannot be verified through this path.
/// </summary>
public class RealOciCrossTargetIntegrationTests
{
    private const string Prefix = "filehub-tests/integration/cross-target/";

    private static (OracleObjectStorageFileHub a, OracleObjectStorageFileHub b, IDisposable client) CreateHubs()
    {
        var configFile = Environment.GetEnvironmentVariable("FILEHUB_OCI_CONFIG_FILE");
        var profile = Environment.GetEnvironmentVariable("FILEHUB_OCI_PROFILE") ?? "DEFAULT";
        var ns = Environment.GetEnvironmentVariable("FILEHUB_OCI_NAMESPACE")!;
        var bucketA = Environment.GetEnvironmentVariable("FILEHUB_OCI_BUCKET")!;
        var bucketB = Environment.GetEnvironmentVariable("FILEHUB_OCI_BUCKET_B")!;

        var provider = string.IsNullOrEmpty(configFile)
            ? new ConfigFileAuthenticationDetailsProvider(profile)
            : new ConfigFileAuthenticationDetailsProvider(configFile, profile);
        var region = provider.Region.RegionId;

        // One shared client → shared CredentialScope → server-side copy.
        var client = new ObjectStorageClient(provider);
        var run = Guid.NewGuid().ToString("N").Substring(0, 8) + "/";

        var hubA = OracleObjectStorageFileHub.FromClient(bucketA, Prefix + run, client, region, ns);
        var hubB = OracleObjectStorageFileHub.FromClient(bucketB, Prefix + run, client, region, ns);
        return (hubA, hubB, client);
    }

    [RequiresOciSecondBucket]
    public void CrossBucket_CopyTo_ServerSide()
    {
        var (hubA, hubB, client) = CreateHubs();
        using var _a = hubA;
        using var _b = hubB;
        using var _c = client;

        var name = $"cross-bucket-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("cross-bucket server-side");
        hubA.Root.CreateFile(name).SetBytes(payload);

        try
        {
            hubA.Root.OpenFile(name).CopyTo(hubB.Root, name);

            var downloaded = hubB.Root.OpenFile(name).ReadAllBytes();
            Assert.Equal(payload, downloaded);

            // Source is untouched by a copy.
            Assert.True(hubA.Root.FileExists(name));
        }
        finally
        {
            TryDelete(() => hubA.Root.OpenFile(name).Delete());
            TryDelete(() => hubB.Root.OpenFile(name).Delete());
        }
    }

    [RequiresOciSecondBucket]
    public void CrossBucket_MoveTo_DeletesSource()
    {
        var (hubA, hubB, client) = CreateHubs();
        using var _a = hubA;
        using var _b = hubB;
        using var _c = client;

        var name = $"cross-bucket-move-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("moving across buckets");
        hubA.Root.CreateFile(name).SetBytes(payload);

        try
        {
            hubA.Root.OpenFile(name).MoveTo(hubB.Root, name);

            Assert.Equal(payload, hubB.Root.OpenFile(name).ReadAllBytes());
            Assert.False(hubA.Root.FileExists(name));
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
