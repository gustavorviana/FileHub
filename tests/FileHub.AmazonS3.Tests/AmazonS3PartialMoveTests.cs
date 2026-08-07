using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

/// <summary>
/// When a move copies successfully but the source delete fails, the driver must
/// surface a <see cref="PartialMoveException"/> — the copy is kept and the caller
/// is told the source still needs removing. Uses the fake's delete-fault injector.
/// </summary>
public class AmazonS3PartialMoveTests
{
    [Fact]
    public async Task File_MoveTo_DeleteFails_ThrowsPartialMove_CopyKept()
    {
        var client = new InMemoryS3Client(bucket: "b", region: "us-east-1");
        using var hub = AmazonS3FileHub.FromS3Client(client);
        var file = hub.Root.CreateFile("a.txt");
        file.SetText("payload");
        client.DeleteFailureInjector = _ => new IOException("delete boom");

        var ex = await Assert.ThrowsAsync<PartialMoveException>(
            () => file.MoveToAsync(hub.Root, "b.txt"));

        Assert.Equal("/a.txt", ex.SourcePath);
        Assert.Equal("/b.txt", ex.DestinationPath);
        Assert.True(hub.Root.FileExists("b.txt"));   // copy landed
        Assert.True(hub.Root.FileExists("a.txt"));   // source kept (delete failed)
    }

    [Fact]
    public async Task Directory_MoveTo_DeleteFails_ThrowsPartialMove_CopyKept()
    {
        var client = new InMemoryS3Client(bucket: "b", region: "us-east-1");
        using var hub = AmazonS3FileHub.FromS3Client(client);
        var dir = hub.Root.CreateDirectory("d1");
        dir.CreateFile("f.txt").SetText("data");
        client.DeleteFailureInjector = _ => new IOException("delete boom");

        await Assert.ThrowsAsync<PartialMoveException>(
            () => hub.Root.OpenDirectory("d1").MoveToAsync(hub.Root, "d2"));

        Assert.True(hub.Root.DirectoryExists("d2"));  // copy landed
    }
}
