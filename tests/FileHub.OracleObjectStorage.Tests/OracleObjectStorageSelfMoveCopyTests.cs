using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// The move/copy-onto-itself business rule for the OCI driver: moving or copying
/// an object onto the exact same namespace+region+bucket+name must fail (never a
/// silent no-op or a self-delete). Runs against the in-memory OCI fake — no
/// network. (The fake keys buckets by namespace+bucket only, so the region
/// dimension of the identity check is asserted at the code/build level, not
/// modeled here; same-hub means same region, which is the case exercised below.)
/// </summary>
public class OracleObjectStorageSelfMoveCopyTests
{
    private static OracleObjectStorageFileHub NewHub()
        => OracleObjectStorageFileHub.FromOciClient(new InMemoryOciClient());

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
