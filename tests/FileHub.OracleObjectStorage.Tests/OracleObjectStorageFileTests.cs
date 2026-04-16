using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageFileTests : IClassFixture<InMemoryOciFixture>
{
    private readonly InMemoryOciFixture _fixture;
    private FileDirectory Root => _fixture.FileHub.Root;

    public OracleObjectStorageFileTests(InMemoryOciFixture fixture) => _fixture = fixture;

    private FileDirectory Scope(string name) => Root.OpenDirectory(name, createIfNotExists: true);

    [Fact]
    public void SetText_ReadAllText_RoundTrip()
    {
        var scope = Scope(nameof(SetText_ReadAllText_RoundTrip));
        var file = scope.CreateFile("hello.txt");
        file.SetText("hello, world");

        Assert.Equal("hello, world", scope.OpenFile("hello.txt").ReadAllText());
    }

    [Fact]
    public void SetBytes_ReadAllBytes_RoundTrip()
    {
        var scope = Scope(nameof(SetBytes_ReadAllBytes_RoundTrip));
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var file = scope.CreateFile("bytes.bin");
        file.SetBytes(data);

        Assert.Equal(data, scope.OpenFile("bytes.bin").ReadAllBytes());
    }

    [Fact]
    public void LargeUpload_ReadChunked_Exercises10MbBoundary()
    {
        var scope = Scope(nameof(LargeUpload_ReadChunked_Exercises10MbBoundary));

        // 25 MB crosses both 10 MB chunk boundaries in OciObjectStream.
        var payload = new byte[25 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var expectedHash = SHA256.HashData(payload);

        var file = scope.CreateFile("big.bin");
        file.SetBytes(payload);

        var reopened = scope.OpenFile("big.bin");
        Assert.Equal(payload.LongLength, reopened.Length);

        using var ms = new MemoryStream();
        using (var src = reopened.GetReadStream())
        {
            var buf = new byte[4096];
            int got;
            while ((got = src.Read(buf, 0, buf.Length)) > 0)
                ms.Write(buf, 0, got);
        }

        Assert.Equal(expectedHash, SHA256.HashData(ms.ToArray()));
    }

    [Fact]
    public void Rename_ChangesObjectName()
    {
        var scope = Scope(nameof(Rename_ChangesObjectName));
        var file = scope.CreateFile("old.txt");
        file.SetText("data");

        file.Rename("new.txt");

        Assert.False(scope.ItemExists("old.txt"));
        Assert.Equal("data", scope.OpenFile("new.txt").ReadAllText());
    }

    [Fact]
    public void MoveTo_DifferentDirectory_CopiesAndDeletes()
    {
        var scope = Scope(nameof(MoveTo_DifferentDirectory_CopiesAndDeletes));
        var srcDir = scope.CreateDirectory("src");
        var dstDir = scope.CreateDirectory("dst");
        var file = srcDir.CreateFile("m.txt");
        file.SetText("moving");

        file.MoveTo(dstDir, "m.txt");

        Assert.False(srcDir.ItemExists("m.txt"));
        Assert.Equal("moving", dstDir.OpenFile("m.txt").ReadAllText());
    }

    [Fact]
    public void CopyTo_SameBucket_UsesFastPath()
    {
        var scope = Scope(nameof(CopyTo_SameBucket_UsesFastPath));
        var srcDir = scope.CreateDirectory("src");
        var dstDir = scope.CreateDirectory("dst");
        var file = srcDir.CreateFile("c.txt");
        file.SetText("copying");

        file.CopyTo(dstDir, "c.txt");

        Assert.Equal("copying", srcDir.OpenFile("c.txt").ReadAllText());
        Assert.Equal("copying", dstDir.OpenFile("c.txt").ReadAllText());
    }

    [Fact]
    public void Delete_RemovesObject()
    {
        var scope = Scope(nameof(Delete_RemovesObject));
        var file = scope.CreateFile("to-remove.txt");
        file.SetText("x");

        file.Delete();
        Assert.False(scope.ItemExists("to-remove.txt"));
    }

    [Fact]
    public void SetLastWriteTime_PersistsViaChangedAtTag()
    {
        var scope = Scope(nameof(SetLastWriteTime_PersistsViaChangedAtTag));
        var file = scope.CreateFile("time.txt");
        file.SetText("t");

        var custom = new DateTime(2024, 1, 15, 10, 20, 30, DateTimeKind.Utc);
        file.SetLastWriteTime(custom);

        var reopened = scope.OpenFile("time.txt");
        Assert.Equal(custom, reopened.LastWriteTimeUtc);
    }

    [Fact]
    public void Second_GetReadStream_Throws_When_Previous_NotDisposed()
    {
        var scope = Scope(nameof(Second_GetReadStream_Throws_When_Previous_NotDisposed));
        var file = scope.CreateFile("single.txt");
        file.SetText("x");

        var first = file.GetReadStream();
        try
        {
            Assert.Throws<InvalidOperationException>(() => file.GetReadStream());
        }
        finally
        {
            first.Dispose();
        }

        using var second = file.GetReadStream();
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ReadAllTextAsync_ReturnsExpected()
    {
        var scope = Scope(nameof(ReadAllTextAsync_ReturnsExpected));
        var file = scope.CreateFile("async.txt");
        await file.SetTextAsync("async-content");

        var reopened = scope.OpenFile("async.txt");
        Assert.Equal("async-content", await reopened.ReadAllTextAsync());
    }

    [Fact]
    public void Extension_ReturnsTail()
    {
        var scope = Scope(nameof(Extension_ReturnsTail));
        var file = scope.CreateFile("doc.txt");
        Assert.Equal(".txt", file.Extension);
    }
}
