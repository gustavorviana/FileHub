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

    [Fact]
    public void DeleteIfExists_ExistingFile_NoHeadProbe()
    {
        var client = new InMemoryS3Client();
        using var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Root.CreateFile("a.txt").SetBytes(new byte[] { 1 });

        var headBefore = client.HeadInvocationCount;
        var listBefore = client.ListInvocationCount;
        var deleteBefore = client.DeleteInvocationCount;

        hub.Root.DeleteIfExists("a.txt");

        // Base impl's FileExists probe (HEAD) is skipped. One LIST is needed to
        // decide between file-leaf and directory-prefix (S3 DeleteObject is
        // idempotent — we can't infer "what was it" from a DELETE alone).
        Assert.Equal(headBefore, client.HeadInvocationCount);
        Assert.Equal(listBefore + 1, client.ListInvocationCount);
        Assert.Equal(deleteBefore + 1, client.DeleteInvocationCount);
        Assert.False(hub.Root.FileExists("a.txt"));
    }

    [Fact]
    public void DeleteIfExists_MissingFile_DoesNotThrow()
    {
        var client = new InMemoryS3Client();
        using var hub = AmazonS3FileHub.FromS3Client(client);

        // No throw even though target never existed — S3 DeleteObject is
        // idempotent and DeleteIfExists swallows any residual FileNotFoundException.
        hub.Root.DeleteIfExists("missing.txt");

        Assert.False(hub.Root.FileExists("missing.txt"));
    }

    [Fact]
    public void Delete_DirectoryWithManyChildren_UsesBatchDeleteObjects()
    {
        // Regression: dir.Delete() used to issue one DeleteObject per child.
        // Now it batches up to 1000 keys per DeleteObjects call — 50 children
        // collapse into 1 round-trip instead of 50.
        var client = new InMemoryS3Client();
        using var hub = AmazonS3FileHub.FromS3Client(client);
        for (int i = 0; i < 50; i++)
            hub.Root.CreateFile($"bulk/{i:D3}.bin").SetBytes(new byte[] { (byte)i });

        var deleteBefore = client.DeleteInvocationCount;
        hub.Root.Delete("bulk");

        // 1 batch delete covers all 50 children + the prefix marker.
        Assert.Equal(deleteBefore + 1, client.DeleteInvocationCount);
        Assert.Equal(0, hub.Root.GetDirectories().Count(d => d.Name == "bulk"));
    }

    [Fact]
    public void Delete_DirectoryName_DeletesAllChildren()
    {
        // Regression: before the fix, S3 DeleteObject's idempotency meant the
        // try/catch FileNotFoundException + directory-fallback was dead code in
        // production, so dir.Delete("sub") silently no-op'd when "sub" was a
        // prefix with children. Now the LIST probe runs first.
        var client = new InMemoryS3Client();
        using var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Root.CreateFile("sub/a.txt").SetBytes(new byte[] { 1 });
        hub.Root.CreateFile("sub/b.txt").SetBytes(new byte[] { 2 });
        hub.Root.CreateFile("sibling.txt").SetBytes(new byte[] { 3 });

        hub.Root.Delete("sub");

        Assert.False(hub.Root.FileExists("sub/a.txt"));
        Assert.False(hub.Root.FileExists("sub/b.txt"));
        Assert.True(hub.Root.FileExists("sibling.txt"));
    }
}
