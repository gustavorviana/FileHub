using System.Linq;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageCrossTargetCopyTests
{
    private static (OracleObjectStorageFileHub hubA, InMemoryOciClient clientA,
                    OracleObjectStorageFileHub hubB, InMemoryOciClient clientB)
        SharedWorldHubs(string nsA = "acct-ns", string bucketA = "alpha", string regionA = "us-test-1",
                        string nsB = "acct-ns", string bucketB = "beta",  string regionB = "us-test-1")
    {
        var world = new InMemoryOciWorld();
        var clientA = world.CreateClient(nsA, bucketA, regionA);
        var clientB = world.CreateClient(nsB, bucketB, regionB);
        var hubA = OracleObjectStorageFileHub.FromOciClient(clientA);
        var hubB = OracleObjectStorageFileHub.FromOciClient(clientB);
        return (hubA, clientA, hubB, clientB);
    }

    [Fact]
    public void File_CopyTo_CrossBucket_SameCredentials_UsesServerSideCopy()
    {
        var (hubA, clientA, hubB, clientB) = SharedWorldHubs();

        var srcDir = hubA.Root.CreateDirectory("src");
        srcDir.CreateFile("doc.txt").SetText("payload");

        srcDir.OpenFile("doc.txt").CopyTo(hubB.Root, "doc.txt");

        Assert.True(clientB.TryGetBody("doc.txt", out var body));
        Assert.Equal("payload", System.Text.Encoding.UTF8.GetString(body));
        Assert.Equal(1, clientA.CopyInvocationCount);
    }

    [Fact]
    public void File_CopyTo_CrossRegion_SameCredentials_UsesServerSideCopy()
    {
        var (hubA, clientA, hubB, clientB) = SharedWorldHubs(
            regionA: "us-ashburn-1", regionB: "eu-frankfurt-1",
            bucketA: "src-bucket",   bucketB: "dst-bucket");

        hubA.Root.CreateFile("r.txt").SetText("cross-region");

        hubA.Root.OpenFile("r.txt").CopyTo(hubB.Root, "r.txt");

        Assert.True(clientB.TryGetBody("r.txt", out var body));
        Assert.Equal("cross-region", System.Text.Encoding.UTF8.GetString(body));
        Assert.Equal(1, clientA.CopyInvocationCount);
    }

    [Fact]
    public void File_CopyTo_CrossNamespace_SameCredentials_UsesServerSideCopy()
    {
        var (hubA, clientA, hubB, clientB) = SharedWorldHubs(nsA: "tenant-a", nsB: "tenant-b");

        hubA.Root.CreateFile("n.txt").SetText("cross-ns");

        hubA.Root.OpenFile("n.txt").CopyTo(hubB.Root, "n.txt");

        Assert.True(clientB.TryGetBody("n.txt", out var body));
        Assert.Equal("cross-ns", System.Text.Encoding.UTF8.GetString(body));
        Assert.Equal(1, clientA.CopyInvocationCount);
    }

    [Fact]
    public void File_CopyTo_DifferentCredentials_FallsBackToStreamCopy()
    {
        var clientA = new InMemoryOciClient(bucket: "alpha");
        var clientB = new InMemoryOciClient(bucket: "beta");
        using var hubA = OracleObjectStorageFileHub.FromOciClient(clientA);
        using var hubB = OracleObjectStorageFileHub.FromOciClient(clientB);

        hubA.Root.CreateFile("stream.txt").SetText("over-the-wire");

        hubA.Root.OpenFile("stream.txt").CopyTo(hubB.Root, "stream.txt");

        Assert.True(clientB.TryGetBody("stream.txt", out var body));
        Assert.Equal("over-the-wire", System.Text.Encoding.UTF8.GetString(body));
        Assert.Equal(0, clientA.CopyInvocationCount);
        Assert.Equal(0, clientB.CopyInvocationCount);
    }

    [Fact]
    public void Directory_CopyTo_CrossBucket_SameCredentials_CopiesAllObjectsAndMarker()
    {
        var (hubA, clientA, hubB, clientB) = SharedWorldHubs();

        var src = hubA.Root.CreateDirectory("tree");
        src.CreateFile("one.txt").SetText("1");
        src.CreateFile("two.txt").SetText("2");
        src.CreateDirectory("nested").CreateFile("three.txt").SetText("3");

        src.CopyTo(hubB.Root, "tree-copy");

        // Destination bucket holds all objects + the marker for tree-copy/.
        Assert.True(clientB.TryGetBody("tree-copy/", out _));
        Assert.True(clientB.TryGetBody("tree-copy/one.txt", out _));
        Assert.True(clientB.TryGetBody("tree-copy/two.txt", out _));
        Assert.True(clientB.TryGetBody("tree-copy/nested/three.txt", out _));

        // Source bucket is untouched.
        Assert.True(clientA.TryGetBody("tree/one.txt", out _));
        Assert.True(clientA.TryGetBody("tree/two.txt", out _));
        Assert.DoesNotContain(clientA.Keys, k => k.StartsWith("tree-copy/"));

        // At least one CopyObject issued per file through the source client.
        Assert.True(clientA.CopyInvocationCount >= 3);
    }

    [Fact]
    public void Directory_CopyTo_DifferentCredentials_FallsBackToStreamCopy()
    {
        var clientA = new InMemoryOciClient(bucket: "alpha");
        var clientB = new InMemoryOciClient(bucket: "beta");
        using var hubA = OracleObjectStorageFileHub.FromOciClient(clientA);
        using var hubB = OracleObjectStorageFileHub.FromOciClient(clientB);

        var src = hubA.Root.CreateDirectory("tree");
        src.CreateFile("one.txt").SetText("1");
        src.CreateDirectory("nested").CreateFile("two.txt").SetText("2");

        src.CopyTo(hubB.Root, "tree-copy");

        Assert.True(clientB.TryGetBody("tree-copy/one.txt", out _));
        Assert.True(clientB.TryGetBody("tree-copy/nested/two.txt", out _));
        Assert.Equal(0, clientA.CopyInvocationCount);
        Assert.Equal(0, clientB.CopyInvocationCount);
    }

    [Fact]
    public void File_MoveTo_CrossBucket_SameCredentials_DeletesSourceAfterCopy()
    {
        var (hubA, clientA, hubB, clientB) = SharedWorldHubs();

        hubA.Root.CreateFile("m.txt").SetText("moving");

        hubA.Root.OpenFile("m.txt").MoveTo(hubB.Root, "m.txt");

        Assert.False(clientA.TryGetBody("m.txt", out _));
        Assert.True(clientB.TryGetBody("m.txt", out var body));
        Assert.Equal("moving", System.Text.Encoding.UTF8.GetString(body));
    }
}
