using System.Collections.Generic;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageMetadataTests : IClassFixture<InMemoryOciFixture>
{
    private readonly InMemoryOciFixture _fixture;
    private FileDirectory Root => _fixture.FileHub.Root;

    public OracleObjectStorageMetadataTests(InMemoryOciFixture fixture) => _fixture = fixture;

    private FileDirectory Scope(string name) => Root.OpenDirectory(name, createIfNotExists: true);

    // N1 — the driver stores the last-write timestamp under the internal
    // ChangedAtTag ("_changedAt") user-metadata key. It must never surface in
    // the user-facing snapshot, neither on the writing instance nor on reopen.
    [Fact]
    public async Task GetMetadata_StripsInternalChangedAtTag()
    {
        var scope = Scope(nameof(GetMetadata_StripsInternalChangedAtTag));
        var file = scope.CreateFile("doc.txt");
        await file.SetTextAsync("payload");

        var sameInstance = await file.GetMetadataAsync();
        Assert.DoesNotContain(OracleObjectStorageFile.ChangedAtTag, sameInstance.Tags.Keys);

        // _changedAt was persisted server-side as user metadata; reading it
        // back via a fresh HEAD must still strip it.
        var reopened = scope.OpenFile("doc.txt");
        var reopenedMeta = await reopened.GetMetadataAsync();
        Assert.DoesNotContain(OracleObjectStorageFile.ChangedAtTag, reopenedMeta.Tags.Keys);
    }

    [Fact]
    public async Task GetMetadata_ReturnsContentTypeAndUserMetadataFromWriteOptions()
    {
        var scope = Scope(nameof(GetMetadata_ReturnsContentTypeAndUserMetadataFromWriteOptions));
        var file = scope.CreateFile("img.bin");

        await file.SetBytesAsync(
            new byte[] { 1, 2, 3 },
            new FileWriteOptions
            {
                ContentType = "image/png",
                CacheControl = "public,max-age=3600",
                Metadata = new Dictionary<string, string> { ["owner"] = "team-x" },
            });

        var meta = await scope.OpenFile("img.bin").GetMetadataAsync();

        Assert.Equal("image/png", meta.ContentType);
        Assert.Equal("public,max-age=3600", meta.CacheControl);
        Assert.Equal("team-x", meta.Tags["owner"]);
        Assert.DoesNotContain(OracleObjectStorageFile.ChangedAtTag, meta.Tags.Keys);
    }

    // N3 — options live with the write stream, not staged on the file. Opening
    // a write stream with options and abandoning it (no bytes written, no
    // commit) must not leak those options into a subsequent plain write.
    [Fact]
    public async Task WriteOptions_AbandonedStream_DoNotLeakIntoNextWrite()
    {
        var scope = Scope(nameof(WriteOptions_AbandonedStream_DoNotLeakIntoNextWrite));
        var file = scope.CreateFile("a.bin");

        using (file.GetWriteStream(new FileWriteOptions { ContentType = "image/png" }))
        {
            // no write → nothing flushed → nothing committed
        }

        await file.SetBytesAsync(new byte[] { 9 });

        var meta = await scope.OpenFile("a.bin").GetMetadataAsync();
        Assert.Null(meta.ContentType);
    }

    // N3 — a committed write's options must not bleed into the next committed
    // write on the same file instance either.
    [Fact]
    public async Task WriteOptions_DoNotCarryAcrossCommittedWrites()
    {
        var scope = Scope(nameof(WriteOptions_DoNotCarryAcrossCommittedWrites));
        var file = scope.CreateFile("b.bin");

        await file.SetBytesAsync(
            new byte[] { 1 },
            new FileWriteOptions { Metadata = new Dictionary<string, string> { ["first"] = "1" } });

        await file.SetBytesAsync(
            new byte[] { 2 },
            new FileWriteOptions { Metadata = new Dictionary<string, string> { ["second"] = "2" } });

        var meta = await scope.OpenFile("b.bin").GetMetadataAsync();
        Assert.Equal("2", meta.Tags["second"]);
    }
}
