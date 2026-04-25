using System.Text;
using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3CrossTargetCopyTests
{
    private static (AmazonS3FileHub hubA, InMemoryS3Client clientA, AmazonS3FileHub hubB, InMemoryS3Client clientB)
        SharedWorldHubs(string bucketA = "alpha", string bucketB = "beta", string regionA = "us-test-1", string regionB = "us-test-1")
    {
        var world = new InMemoryS3World();
        var clientA = world.CreateClient(bucketA, regionA);
        var clientB = world.CreateClient(bucketB, regionB);
        var hubA = AmazonS3FileHub.FromS3Client(clientA);
        var hubB = AmazonS3FileHub.FromS3Client(clientB);
        return (hubA, clientA, hubB, clientB);
    }

    [Fact]
    public void File_CopyTo_CrossBucket_SameCredentials_UsesServerSideCopy()
    {
        var (hubA, clientA, hubB, clientB) = SharedWorldHubs();

        hubA.Root.CreateFile("doc.txt").SetText("payload");
        hubA.Root.OpenFile("doc.txt").CopyTo(hubB.Root, "doc.txt");

        Assert.True(clientB.TryGetBody("doc.txt", out var body));
        Assert.Equal("payload", Encoding.UTF8.GetString(body));
        // CopyObject is issued by the destination client — that's what makes
        // cross-region work because the request hits the destination endpoint.
        Assert.Equal(0, clientA.CopyInvocationCount);
        Assert.Equal(1, clientB.CopyInvocationCount);
    }

    [Fact]
    public void File_CopyTo_CrossRegion_SameCredentials_UsesServerSideCopy()
    {
        var world = new Fakes.InMemoryS3World();
        var clientA = world.CreateClient(bucket: "src-bucket", region: "us-east-1");
        var clientB = world.CreateClient(bucket: "dst-bucket", region: "eu-west-1");
        using var hubA = AmazonS3FileHub.FromS3Client(clientA);
        using var hubB = AmazonS3FileHub.FromS3Client(clientB);

        hubA.Root.CreateFile("artifact.bin").SetText("cross-region payload");
        hubA.Root.OpenFile("artifact.bin").CopyTo(hubB.Root, "artifact.bin");

        Assert.True(clientB.TryGetBody("artifact.bin", out var body));
        Assert.Equal("cross-region payload", Encoding.UTF8.GetString(body));
        // Destination client (eu-west-1) issued the CopyObject, reading from
        // the source bucket via x-amz-copy-source. No byte transfer through
        // this process — pure server-side.
        Assert.Equal(0, clientA.CopyInvocationCount);
        Assert.Equal(1, clientB.CopyInvocationCount);
    }

    [Fact]
    public void File_CopyTo_DifferentCredentials_FallsBackToStreamCopy()
    {
        var clientA = new InMemoryS3Client(bucket: "alpha");
        var clientB = new InMemoryS3Client(bucket: "beta");
        using var hubA = AmazonS3FileHub.FromS3Client(clientA);
        using var hubB = AmazonS3FileHub.FromS3Client(clientB);

        hubA.Root.CreateFile("stream.txt").SetText("over-the-wire");
        hubA.Root.OpenFile("stream.txt").CopyTo(hubB.Root, "stream.txt");

        Assert.True(clientB.TryGetBody("stream.txt", out var body));
        Assert.Equal("over-the-wire", Encoding.UTF8.GetString(body));
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
        Assert.Equal("moving", Encoding.UTF8.GetString(body));
    }
}
