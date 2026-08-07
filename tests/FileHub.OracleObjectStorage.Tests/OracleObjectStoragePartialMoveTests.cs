using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// When a move copies successfully but the source delete fails, the driver must
/// surface a <see cref="PartialMoveException"/>. OCI uses a native rename within
/// the same bucket (no separate delete), so the copy+delete path is exercised via
/// a cross-credential move, with the source client's delete-fault injector.
/// </summary>
public class OracleObjectStoragePartialMoveTests
{
    [Fact]
    public async Task File_MoveTo_CrossCredential_DeleteFails_ThrowsPartialMove_CopyKept()
    {
        var source = new InMemoryOciClient(bucket: "alpha");
        var dest = new InMemoryOciClient(bucket: "beta");
        using var hubA = OracleObjectStorageFileHub.FromOciClient(source);
        using var hubB = OracleObjectStorageFileHub.FromOciClient(dest);
        var file = hubA.Root.CreateFile("a.txt");
        file.SetText("payload");
        source.DeleteFailureInjector = _ => new IOException("delete boom");

        var ex = await Assert.ThrowsAsync<PartialMoveException>(
            () => file.MoveToAsync(hubB.Root, "a.txt"));

        Assert.True(hubB.Root.FileExists("a.txt"));   // copy landed on the destination
        Assert.True(hubA.Root.FileExists("a.txt"));   // source kept (delete failed)
    }
}
