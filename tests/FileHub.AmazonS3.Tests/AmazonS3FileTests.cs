using System.Text;
using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3FileTests
{
    private static AmazonS3FileHub NewHub(out InMemoryS3Client client)
    {
        client = new InMemoryS3Client();
        return AmazonS3FileHub.FromS3Client(client);
    }

    [Fact]
    public void CreateFile_ReadBack_RoundTrips()
    {
        using var hub = NewHub(out var client);

        var file = hub.Root.CreateFile("hello.txt");
        file.SetText("world");

        Assert.Equal("world", hub.Root.OpenFile("hello.txt").ReadAllText());
        Assert.True(client.TryGetBody("hello.txt", out var body));
        Assert.Equal("world", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void Delete_RemovesObjectFromStore()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("a.txt").SetText("x");

        hub.Root.OpenFile("a.txt").Delete();

        Assert.False(client.TryGetBody("a.txt", out _));
    }

    [Fact]
    public void Rename_InPlace_UsesCopyAndDelete()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("old.txt").SetText("data");

        var file = hub.Root.OpenFile("old.txt");
        file.Rename("new.txt");

        Assert.False(client.TryGetBody("old.txt", out _));
        Assert.True(client.TryGetBody("new.txt", out var body));
        Assert.Equal("data", Encoding.UTF8.GetString(body));
        Assert.Equal(1, client.CopyInvocationCount);
    }

    [Fact]
    public void Length_UpdatedAfterWrite_NoRefresh()
    {
        using var hub = NewHub(out _);
        var file = hub.Root.CreateFile("sized.txt");
        file.SetBytes(new byte[123]);

        Assert.Equal(123, file.Length);
    }

    [Fact]
    public void CopyTo_SameBucket_UsesServerSideCopyObject()
    {
        using var hub = NewHub(out var client);
        var src = hub.Root.CreateDirectory("src");
        var dst = hub.Root.CreateDirectory("dst");
        src.CreateFile("doc.txt").SetText("content");

        src.OpenFile("doc.txt").CopyTo(dst, "doc.txt");

        Assert.True(client.TryGetBody("dst/doc.txt", out var body));
        Assert.Equal("content", Encoding.UTF8.GetString(body));
        Assert.Equal(1, client.CopyInvocationCount);
    }

    [Fact]
    public void MoveTo_SameBucket_UsesCopyThenDelete()
    {
        using var hub = NewHub(out var client);
        var src = hub.Root.CreateDirectory("src");
        var dst = hub.Root.CreateDirectory("dst");
        src.CreateFile("m.txt").SetText("moving");

        src.OpenFile("m.txt").MoveTo(dst, "m.txt");

        Assert.False(client.TryGetBody("src/m.txt", out _));
        Assert.True(client.TryGetBody("dst/m.txt", out var body));
        Assert.Equal("moving", Encoding.UTF8.GetString(body));
        Assert.Equal(1, client.CopyInvocationCount);
    }

    [Fact]
    public void MoveTo_DeleteFails_ThrowsPartialMoveException()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("p.txt").SetText("payload");
        client.DeleteFailureInjector = _ => new System.UnauthorizedAccessException("nope");

        var source = hub.Root.OpenFile("p.txt");
        var dst = hub.Root.CreateDirectory("dst");

        var ex = Assert.Throws<FileHub.PartialMoveException>(() => source.MoveTo(dst, "p.txt"));
        Assert.Equal("/p.txt", ex.SourcePath);
        Assert.Equal("/dst/p.txt", ex.DestinationPath);
        Assert.IsType<System.UnauthorizedAccessException>(ex.InnerException);

        // File exists in both places
        Assert.True(client.TryGetBody("p.txt", out _));
        Assert.True(client.TryGetBody("dst/p.txt", out _));
    }
}
