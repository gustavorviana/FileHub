using FileHub.AmazonS3.Tests.Fakes;
using System.Text;

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
    public void Exists_DetectsFileAndDirectory_InOneListCall()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("report.txt").SetText("x");
        hub.Root.CreateDirectory("logs").CreateFile("app.log").SetText("y");

        var listsBefore = client.ListInvocationCount;

        Assert.True(hub.Root.Exists("report.txt"));   // file
        Assert.True(hub.Root.Exists("logs"));         // directory
        Assert.False(hub.Root.Exists("missing"));     // neither
        // A sibling that merely shares the "report" prefix must not match.
        Assert.False(hub.Root.Exists("rep"));

        // No HEAD probes — Exists answers purely via LIST, one per call.
        Assert.Equal(0, client.HeadInvocationCount);
        Assert.Equal(listsBefore + 4, client.ListInvocationCount);
    }

    [Fact]
    public void Rename_ToExistingName_ThrowsAndKeepsBoth()
    {
        using var hub = NewHub(out _);
        hub.Root.CreateFile("a.txt").SetText("a");
        hub.Root.CreateFile("b.txt").SetText("b");

        var file = hub.Root.OpenFile("a.txt");
        Assert.Throws<FileAlreadyExistsException>(() => file.Rename("b.txt"));
        Assert.Equal("a", hub.Root.OpenFile("a.txt").ReadAllText());
        Assert.Equal("b", hub.Root.OpenFile("b.txt").ReadAllText());
    }

    [Fact]
    public void Rename_NestedName_MovesToSubPathKey()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("a.txt").SetText("data");

        var moved = hub.Root.OpenFile("a.txt").Rename("sub/deep/b.txt");

        Assert.Equal("b.txt", moved.Name);
        Assert.False(client.TryGetBody("a.txt", out _));            // source key gone
        Assert.True(client.TryGetBody("sub/deep/b.txt", out var body));
        Assert.Equal("data", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void CopyTo_NestedName_WritesSubPathKey()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("a.txt").SetText("data");

        var copy = hub.Root.OpenFile("a.txt").CopyTo(hub.Root, "x/y/z.txt");

        Assert.Equal("z.txt", copy.Name);
        Assert.True(client.TryGetBody("a.txt", out _));             // source kept
        Assert.True(client.TryGetBody("x/y/z.txt", out var body));
        Assert.Equal("data", Encoding.UTF8.GetString(body));
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
