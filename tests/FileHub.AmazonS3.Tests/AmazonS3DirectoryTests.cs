using System.Linq;
using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3DirectoryTests
{
    private static AmazonS3FileHub NewHub() =>
        AmazonS3FileHub.FromS3Client(new InMemoryS3Client());

    [Fact]
    public void CreateDirectory_CreatesMarkerObject()
    {
        using var hub = NewHub();

        var dir = hub.Root.CreateDirectory("folder");

        Assert.Equal("/folder", dir.Path);
        Assert.True(hub.Root.TryOpenDirectory("folder", out _));
    }

    [Fact]
    public void CreateDirectory_NestedPath_DirectMode_OnePut()
    {
        using var hub = NewHub();

        var deep = hub.Root.CreateDirectory("a/b/c");

        Assert.Equal("/a/b/c", deep.Path);
        Assert.True(hub.Root.TryOpenDirectory("a/b/c", out _));
    }

    [Fact]
    public void GetDirectories_EnumeratesImmediateChildren()
    {
        using var hub = NewHub();
        hub.Root.CreateDirectory("a");
        hub.Root.CreateDirectory("b");
        hub.Root.CreateDirectory("a/nested"); // should not show at root

        var children = hub.Root.GetDirectories().Select(d => d.Name).OrderBy(s => s).ToArray();

        Assert.Equal(new[] { "a", "b" }, children);
    }

    [Fact]
    public void GetFiles_FiltersByPattern()
    {
        using var hub = NewHub();
        hub.Root.CreateFile("a.txt").SetText("1");
        hub.Root.CreateFile("b.log").SetText("2");
        hub.Root.CreateFile("c.txt").SetText("3");

        var txts = hub.Root.GetFiles("*.txt").Select(f => f.Name).OrderBy(s => s).ToArray();

        Assert.Equal(new[] { "a.txt", "c.txt" }, txts);
    }

    [Fact]
    public void DeleteDirectory_RemovesAllObjectsUnderPrefix()
    {
        using var hub = NewHub();
        var dir = hub.Root.CreateDirectory("deleteme");
        dir.CreateFile("x.txt").SetText("1");
        dir.CreateDirectory("sub").CreateFile("y.txt").SetText("2");

        dir.Delete();

        Assert.False(hub.Root.TryOpenDirectory("deleteme", out _));
    }

    [Fact]
    public void ItemExists_File_ReturnsTrue()
    {
        using var hub = NewHub();
        hub.Root.CreateFile("f.txt").SetText(".");

        Assert.True(hub.Root.FileExists("f.txt"));
        Assert.False(hub.Root.FileExists("g.txt"));
    }

    [Fact]
    public void DeleteRoot_Throws()
    {
        using var hub = NewHub();
        Assert.Throws<System.NotSupportedException>(() => hub.Root.Delete());
    }
}
