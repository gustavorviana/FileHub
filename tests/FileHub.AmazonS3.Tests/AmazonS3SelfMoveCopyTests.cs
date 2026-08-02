using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

/// <summary>
/// The move/copy-onto-itself business rule for the S3 driver: moving or copying
/// an object onto the exact same bucket+key must fail (never a silent no-op or a
/// self-delete). Runs against the in-memory S3 fake — no network.
/// </summary>
public class AmazonS3SelfMoveCopyTests
{
    private static AmazonS3FileHub NewHub()
        => AmazonS3FileHub.FromS3Client(new InMemoryS3Client(bucket: "b", region: "us-east-1"));

    [Fact]
    public void File_CopyTo_OntoItself_Throws_AndKeepsFile()
    {
        using var hub = NewHub();
        var file = hub.Root.CreateFile("a.txt");
        file.SetText("keep");

        Assert.Throws<FileAlreadyExistsException>(() => file.CopyTo(hub.Root, "a.txt"));

        Assert.True(hub.Root.FileExists("a.txt"));
    }

    [Fact]
    public void File_MoveTo_OntoItself_Throws_AndKeepsFile()
    {
        using var hub = NewHub();
        var file = hub.Root.CreateFile("a.txt");
        file.SetText("keep");

        Assert.Throws<FileAlreadyExistsException>(() => file.MoveTo(hub.Root, "a.txt"));

        Assert.True(hub.Root.FileExists("a.txt"));
    }

    [Fact]
    public void File_CopyTo_DifferentName_SameBucket_IsNotBlocked()
    {
        using var hub = NewHub();
        var file = hub.Root.CreateFile("a.txt");
        file.SetText("payload");

        file.CopyTo(hub.Root, "b.txt");

        Assert.True(hub.Root.FileExists("a.txt"));
        Assert.True(hub.Root.FileExists("b.txt"));
    }

    [Fact]
    public void Directory_CopyTo_OntoItself_Throws()
    {
        using var hub = NewHub();
        var dir = hub.Root.CreateDirectory("d1");
        dir.CreateFile("f.txt").SetText("data");

        Assert.Throws<FileAlreadyExistsException>(() => hub.Root.OpenDirectory("d1").CopyTo(hub.Root, "d1"));
    }

    [Fact]
    public void Directory_MoveTo_OntoItself_Throws()
    {
        using var hub = NewHub();
        var dir = hub.Root.CreateDirectory("d1");
        dir.CreateFile("f.txt").SetText("data");

        Assert.Throws<FileAlreadyExistsException>(() => hub.Root.OpenDirectory("d1").MoveTo(hub.Root, "d1"));
    }

    [Fact]
    public void Directory_MoveTo_IntoOwnDescendant_Throws()
    {
        using var hub = NewHub();
        var outer = hub.Root.CreateDirectory("outer");
        var inner = outer.CreateDirectory("inner");

        Assert.Throws<FileHubException>(() => outer.MoveTo(inner, "moved"));
    }

    [Fact]
    public void Directory_CopyTo_IntoOwnDescendant_Throws()
    {
        using var hub = NewHub();
        var outer = hub.Root.CreateDirectory("outer");
        var inner = outer.CreateDirectory("inner");

        Assert.Throws<FileHubException>(() => outer.CopyTo(inner, "copied"));
    }
}
