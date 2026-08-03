using System.Security.Cryptography;

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

        // 25 MB crosses both 10 MB chunk boundaries in OciReadStream.
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

        Assert.False(scope.FileExists("old.txt"));
        Assert.Equal("data", scope.OpenFile("new.txt").ReadAllText());
    }

    [Fact]
    public void Rename_ToExistingName_ThrowsAndKeepsBoth()
    {
        var scope = Scope(nameof(Rename_ToExistingName_ThrowsAndKeepsBoth));
        scope.CreateFile("a.txt").SetText("a");
        scope.CreateFile("b.txt").SetText("b");

        var file = scope.OpenFile("a.txt");
        Assert.Throws<FileAlreadyExistsException>(() => file.Rename("b.txt"));
        Assert.Equal("a", scope.OpenFile("a.txt").ReadAllText());
        Assert.Equal("b", scope.OpenFile("b.txt").ReadAllText());
    }

    [Fact]
    public void Rename_NestedName_Throws()
    {
        var scope = Scope(nameof(Rename_NestedName_Throws));
        scope.CreateFile("a.txt").SetText("data");

        Assert.Throws<ArgumentException>(() => scope.OpenFile("a.txt").Rename("sub/deep/b.txt"));

        Assert.True(scope.FileExists("a.txt"));
    }

    [Fact]
    public void CopyTo_NestedName_WritesSubPathKey()
    {
        var scope = Scope(nameof(CopyTo_NestedName_WritesSubPathKey));
        scope.CreateFile("a.txt").SetText("data");

        var copy = scope.OpenFile("a.txt").CopyTo(scope, "x/y/z.txt");

        Assert.Equal("z.txt", copy.Name);
        Assert.Equal("data", scope.OpenFile("a.txt").ReadAllText());
        Assert.Equal("data", scope.OpenFile("x/y/z.txt").ReadAllText());
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

        Assert.False(srcDir.FileExists("m.txt"));
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
        Assert.False(scope.FileExists("to-remove.txt"));
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
