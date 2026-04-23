using System.IO;
using System.Text;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// Contract tests for <see cref="IRefreshable"/>: the OCI driver must NOT
/// perform hidden I/O inside property getters, and must expose explicit sync
/// + async refresh entry points so callers avoid async-over-sync deadlock
/// risk in UI / ASP.NET <c>SynchronizationContext</c>s.
/// </summary>
public class OciRefreshableTests : IClassFixture<InMemoryOciFixture>
{
    private readonly InMemoryOciFixture _fixture;
    private FileDirectory Root => _fixture.FileHub.Root;

    public OciRefreshableTests(InMemoryOciFixture fixture) => _fixture = fixture;

    private FileDirectory Scope(string name) => Root.OpenDirectory(name, createIfNotExists: true);

    [Fact]
    public void OracleObjectStorageFile_ImplementsIRefreshable()
    {
        var scope = Scope(nameof(OracleObjectStorageFile_ImplementsIRefreshable));
        var file = scope.CreateFile("x.txt");

        Assert.IsAssignableFrom<IRefreshable>(file);
    }

    [Fact]
    public void OracleObjectStorageDirectory_ImplementsIRefreshable()
    {
        Assert.IsAssignableFrom<IRefreshable>(Root);
    }

    [Fact]
    public void FileLength_IsUpdatedLocallyAfterWrite_WithoutRefresh()
    {
        var scope = Scope(nameof(FileLength_IsUpdatedLocallyAfterWrite_WithoutRefresh));
        var file = scope.CreateFile("a.txt");

        file.SetText("hello-world");

        // The write pipeline updates the cached length as bytes are buffered;
        // no refresh round-trip needed.
        Assert.Equal(11, file.Length);
    }

    [Fact]
    public async Task RefreshAsync_OnFile_SyncsMetadataFromServer()
    {
        var scope = Scope(nameof(RefreshAsync_OnFile_SyncsMetadataFromServer));
        var file = scope.CreateFile("a.txt");
        file.SetText("hi");

        // Mutate the bucket behind the driver's back to simulate an external change.
        var objectName = $"{nameof(RefreshAsync_OnFile_SyncsMetadataFromServer)}/a.txt";
        await _fixture.Client.PutObjectAsync(
            objectName,
            new MemoryStream(Encoding.UTF8.GetBytes("completely-new-content")),
            contentLength: 22,
            contentType: null!,
            opcMeta: null!);

        Assert.Equal(2, file.Length);

        await ((IRefreshable)file).RefreshAsync();

        Assert.Equal(22, file.Length);
    }

    [Fact]
    public async Task PublicRefresh_IsSyncDelegatingToAsync()
    {
        var scope = Scope(nameof(PublicRefresh_IsSyncDelegatingToAsync));
        var file = scope.CreateFile("a.txt");
        file.SetText("hi");

        var objectName = $"{nameof(PublicRefresh_IsSyncDelegatingToAsync)}/a.txt";
        await _fixture.Client.PutObjectAsync(
            objectName,
            new MemoryStream(new byte[] { 1, 2, 3, 4 }),
            contentLength: 4,
            contentType: null!,
            opcMeta: null!);

        // Sync call — this is what we're verifying: no deadlock, result reflects server state.
        ((IRefreshable)file).Refresh();

        Assert.Equal(4, file.Length);
    }

    [Fact]
    public void PropertyGetters_DoNotHitNetwork_BeforeRefresh()
    {
        var scope = Scope(nameof(PropertyGetters_DoNotHitNetwork_BeforeRefresh));
        var dir = scope.CreateDirectory("fresh");

        // Peek before any Refresh — getters return default metadata, no blocking I/O.
        var creation = dir.CreationTimeUtc;
        var modified = dir.LastWriteTimeUtc;

        Assert.Equal(default, creation);
        Assert.Equal(default, modified);
    }

    /// <summary>
    /// Needs a hub rooted at a non-empty prefix so the shared class fixture
    /// doesn't fit — this test opts out and builds its own isolated hub.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_OnRoot_CreatesMarkerIfMissingForNonEmptyPrefix()
    {
        using var client = new InMemoryOciClient();

        using var hub = await OracleObjectStorageFileHub.FromOciClientAsync(client, "uploads/2026");

        Assert.True(client.TryGetBody("uploads/2026/", out _));
    }
}
