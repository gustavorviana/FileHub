using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FileHub.Ftp.Tests.Integration;

/// <summary>
/// End-to-end tests that talk to a real FTP server (vsftpd in a short-lived
/// Docker container supplied by <see cref="FtpServerFixture"/>). Exercises the
/// whole driver surface against the real wire protocol — PASV negotiation,
/// <c>STOR</c> / <c>RETR</c> data channels, <c>RNFR/RNTO</c>, <c>MKD</c>,
/// <c>RMD</c>, <c>LIST</c>. Skipped when Docker is not reachable.
/// </summary>
public class FtpIntegrationTests : IClassFixture<FtpServerFixture>
{
    private readonly FtpServerFixture _ftp;

    public FtpIntegrationTests(FtpServerFixture ftp) => _ftp = ftp;

    private Task<FtpFileHub> NewHubAsync() =>
        FtpFileHub.ConnectAsync(
            host:     _ftp.Host,
            port:     _ftp.Port,
            user:     FtpServerFixture.User,
            password: FtpServerFixture.Password,
            rootPath: "/");

    /// <summary>Per-test directory scoping so state from one test doesn't leak into the next.</summary>
    private static async Task<FileDirectory> ScopeAsync(FtpFileHub hub, string name)
    {
        if (hub.Root.TryOpenDirectory(name, out var existing))
        {
            existing.Delete();
        }
        return await hub.Root.CreateDirectoryAsync(name);
    }

    [RequiresDockerFact]
    public async Task Connect_ThenListRoot_Works()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();

        // Listing the root should succeed without throwing, regardless of
        // what's in it. Sanity check for PASV/LIST.
        _ = hub.Root.GetFiles().ToList();
        _ = hub.Root.GetDirectories().ToList();
    }

    [RequiresDockerFact]
    public async Task CreateFile_WriteText_ReadBack_Roundtrip()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(CreateFile_WriteText_ReadBack_Roundtrip));

        var file = scope.CreateFile("hello.txt");
        file.SetText("alpha-beta-gamma", Encoding.UTF8);

        Assert.Equal("alpha-beta-gamma", scope.OpenFile("hello.txt").ReadAllText());
        Assert.Equal(Encoding.UTF8.GetByteCount("alpha-beta-gamma"), file.Length);
    }

    [RequiresDockerFact]
    public async Task CreateFile_WriteBytes_ReadBack_Roundtrip()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(CreateFile_WriteBytes_ReadBack_Roundtrip));

        var payload = new byte[] { 0x01, 0x02, 0x03, 0xFE, 0xFF };
        scope.CreateFile("blob.bin").SetBytes(payload);

        Assert.Equal(payload, scope.OpenFile("blob.bin").ReadAllBytes());
    }

    [RequiresDockerFact]
    public async Task LargeStream_RoundTripsByteForByte()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(LargeStream_RoundTripsByteForByte));

        // 2 MB — crosses typical socket buffer sizes without being painfully slow.
        var payload = new byte[2 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);

        var file = scope.CreateFile("big.bin");
        using (var write = file.GetWriteStream())
            await write.WriteAsync(payload);

        using var ms = new MemoryStream();
        using (var read = scope.OpenFile("big.bin").GetReadStream())
            await read.CopyToAsync(ms);

        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(ms.ToArray()));
    }

    [RequiresDockerFact]
    public async Task CreateDirectory_Nested_CreatesEachSegment()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(CreateDirectory_Nested_CreatesEachSegment));

        var leaf = scope.CreateDirectory("a/b/c");

        Assert.Equal("c", leaf.Name);
        Assert.True(scope.TryOpenDirectory("a", out _));
        Assert.True(scope.TryOpenDirectory("a/b", out _));
        Assert.True(scope.TryOpenDirectory("a/b/c", out _));
    }

    [RequiresDockerFact]
    public async Task GetFiles_PatternAndLimit_Paginate()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(GetFiles_PatternAndLimit_Paginate));

        foreach (var n in new[] { "a.txt", "b.txt", "c.log", "d.txt" })
            scope.CreateFile(n).SetText(n);

        var firstTwoTxt = scope.GetFiles("*.txt", limit: 2).Select(f => f.Name).ToArray();
        Assert.Equal(new[] { "a.txt", "b.txt" }, firstTwoTxt);

        var afterNamed = scope.GetFiles(offset: FileListOffset.FromName("b.txt"), limit: 10)
            .Select(f => f.Name).ToArray();
        Assert.Contains("b.txt", afterNamed);
        Assert.Contains("d.txt", afterNamed);
        Assert.DoesNotContain("a.txt", afterNamed);
    }

    [RequiresDockerFact]
    public async Task GetDirectories_ReturnsOnlyImmediateChildren()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(GetDirectories_ReturnsOnlyImmediateChildren));

        scope.CreateDirectory("alpha").CreateDirectory("nested");
        scope.CreateDirectory("beta");
        scope.CreateFile("readme.md").SetText("x");

        var names = scope.GetDirectories().Select(d => d.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, names);
    }

    [RequiresDockerFact]
    public async Task Rename_File_ChangesNameOnServer()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(Rename_File_ChangesNameOnServer));

        var file = scope.CreateFile("old.txt");
        file.SetText("payload");
        file.Rename("new.txt");

        Assert.False(scope.ItemExists("old.txt"));
        Assert.Equal("payload", scope.OpenFile("new.txt").ReadAllText());
    }

    [RequiresDockerFact]
    public async Task MoveTo_SameConnection_UsesServerSideRename()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(MoveTo_SameConnection_UsesServerSideRename));

        var dst = scope.CreateDirectory("dst");
        var file = scope.CreateFile("m.txt");
        file.SetText("moving");

        file.MoveTo(dst, "m.txt");

        Assert.False(scope.ItemExists("m.txt"));
        Assert.Equal("moving", dst.OpenFile("m.txt").ReadAllText());
    }

    [RequiresDockerFact]
    public async Task CopyTo_StreamCopy_DuplicatesFile()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(CopyTo_StreamCopy_DuplicatesFile));

        var dst = scope.CreateDirectory("dst");
        var src = scope.CreateFile("c.txt");
        src.SetText("copying");

        src.CopyTo(dst, "c.txt");

        // Source still there — FTP has no server-side copy, driver streams through.
        Assert.Equal("copying", scope.OpenFile("c.txt").ReadAllText());
        Assert.Equal("copying", dst.OpenFile("c.txt").ReadAllText());
    }

    [RequiresDockerFact]
    public async Task Delete_File_RemovesIt()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(Delete_File_RemovesIt));

        scope.CreateFile("victim.txt").SetText("x");
        Assert.True(scope.ItemExists("victim.txt"));

        scope.OpenFile("victim.txt").Delete();
        Assert.False(scope.ItemExists("victim.txt"));
    }

    [RequiresDockerFact]
    public async Task Delete_Directory_Recursive()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(Delete_Directory_Recursive));

        var sub = scope.CreateDirectory("to-delete");
        sub.CreateFile("inner.txt").SetText("x");
        sub.CreateDirectory("deeper").CreateFile("leaf.txt").SetText("y");

        sub.Delete();

        Assert.False(scope.ItemExists("to-delete"));
    }

    [RequiresDockerFact]
    public async Task Refresh_ReflectsExternalMutation()
    {
        if (_ftp.SkipReason != null) return;

        using var hub = await NewHubAsync();
        var scope = await ScopeAsync(hub, nameof(Refresh_ReflectsExternalMutation));

        var file = scope.CreateFile("data.txt");
        file.SetText("hi");

        Assert.Equal(2, file.Length);

        // Overwrite out-of-band through a second hub — same server, same path.
        using (var otherHub = await NewHubAsync())
        {
            var otherScope = otherHub.Root.OpenDirectory(nameof(Refresh_ReflectsExternalMutation));
            otherScope.OpenFile("data.txt").SetText("completely-new-content");
        }

        // Cached length is still stale until we ask for a refresh.
        Assert.Equal(2, file.Length);

        await ((IRefreshable)file).RefreshAsync();
        Assert.Equal(22, file.Length);
    }
}
