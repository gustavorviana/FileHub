using System;
using System.IO;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageOpenFileTests
{
    private static OracleObjectStorageFileHub NewHub(out InMemoryOciClient client)
    {
        client = new InMemoryOciClient();
        return OracleObjectStorageFileHub.FromOciClient(client);
    }

    // === Strict (createIfNotExists: false) — always HEADs ===

    [Fact]
    public void Strict_Existing_FiresOneHead_AndLoadsState()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("x.txt").SetText("hello");
        var putsAfterSetup = client.PutInvocationCount;
        var headsBefore = client.HeadInvocationCount;

        var opened = (OracleObjectStorageFile)hub.Root.OpenFile("x.txt");

        Assert.Equal(headsBefore + 1, client.HeadInvocationCount);
        Assert.True(opened.IsLoaded);
        Assert.Equal(5, opened.Length);
        Assert.Equal(putsAfterSetup, client.PutInvocationCount);
    }

    [Fact]
    public void Strict_Missing_ThrowsAfterHead()
    {
        using var hub = NewHub(out var client);
        var headsBefore = client.HeadInvocationCount;

        Assert.Throws<FileNotFoundException>(() => hub.Root.OpenFile("ghost.txt"));

        Assert.Equal(headsBefore + 1, client.HeadInvocationCount);
    }

    // === createIfNotExists: true — zero server calls ===

    [Fact]
    public void CreateIfNotExists_Existing_ZeroCalls_ReturnsStub()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("exists.txt").SetText("payload");
        var heads = client.HeadInvocationCount;
        var puts = client.PutInvocationCount;

        var file = (OracleObjectStorageFile)hub.Root.OpenFile("exists.txt", createIfNotExists: true);

        Assert.Equal(heads, client.HeadInvocationCount);
        Assert.Equal(puts, client.PutInvocationCount);
        Assert.False(file.IsLoaded);
        Assert.Equal(-1, file.Length);
    }

    [Fact]
    public void CreateIfNotExists_Missing_ZeroCalls_ReturnsStub()
    {
        using var hub = NewHub(out var client);
        var heads = client.HeadInvocationCount;
        var puts = client.PutInvocationCount;

        var file = (OracleObjectStorageFile)hub.Root.OpenFile("ghost.txt", createIfNotExists: true);

        Assert.Equal(heads, client.HeadInvocationCount);
        Assert.Equal(puts, client.PutInvocationCount);
        Assert.False(file.IsLoaded);
        Assert.Equal(-1, file.Length);
    }

    [Fact]
    public void CreateIfNotExists_Write_CreatesWithOnePut_AndLoads()
    {
        using var hub = NewHub(out var client);
        var file = (OracleObjectStorageFile)hub.Root.OpenFile("new.txt", createIfNotExists: true);
        var putsBefore = client.PutInvocationCount;

        file.SetText("content");

        Assert.Equal(putsBefore + 1, client.PutInvocationCount);
        Assert.True(file.IsLoaded);
        Assert.Equal(7, file.Length);
        Assert.True(client.TryGetBody("new.txt", out var body));
        Assert.Equal("content", System.Text.Encoding.UTF8.GetString(body));
    }

    // === Stub Exists() ===

    [Fact]
    public void Stub_Exists_Hit_LoadsState()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("real.txt").SetText("alive");
        var file = (OracleObjectStorageFile)hub.Root.OpenFile("real.txt", createIfNotExists: true);
        Assert.False(file.IsLoaded);
        var headsBefore = client.HeadInvocationCount;

        var exists = file.Exists();

        Assert.True(exists);
        Assert.Equal(headsBefore + 1, client.HeadInvocationCount);
        Assert.True(file.IsLoaded);
        Assert.Equal(5, file.Length);
    }

    [Fact]
    public void Stub_Exists_Miss_ReturnsFalse_AndStaysUnloaded()
    {
        using var hub = NewHub(out var client);
        var file = (OracleObjectStorageFile)hub.Root.OpenFile("ghost.txt", createIfNotExists: true);
        var headsBefore = client.HeadInvocationCount;

        var exists = file.Exists();

        Assert.False(exists);
        Assert.Equal(headsBefore + 1, client.HeadInvocationCount);
        Assert.False(file.IsLoaded);
        Assert.Equal(-1, file.Length);
    }

    // === TryOpenFile — always HEADs ===

    [Fact]
    public void TryOpenFile_AlwaysFiresHead_AndLoadsIfFound()
    {
        using var hub = NewHub(out var client);
        hub.Root.CreateFile("t.txt").SetText("t");
        var heads = client.HeadInvocationCount;

        var ok = hub.Root.TryOpenFile("t.txt", out var file);

        Assert.True(ok);
        Assert.Equal(heads + 1, client.HeadInvocationCount);
        Assert.True(((OracleObjectStorageFile)file).IsLoaded);
    }

    // === CreateFile — explicit empty PUT preserved ===

    [Fact]
    public void CreateFile_DoesEmptyPut_AndFileIsLoaded()
    {
        using var hub = NewHub(out var client);
        var puts = client.PutInvocationCount;

        var file = (OracleObjectStorageFile)hub.Root.CreateFile("new.txt");

        Assert.Equal(puts + 1, client.PutInvocationCount);
        Assert.True(file.IsLoaded);
        Assert.Equal(0, file.Length);
    }

    // === Nested paths — intermediate "directories" must not hit the wire ===

    [Fact]
    public void CreateFile_NestedPath_OnlyOnePutForLeaf()
    {
        using var hub = NewHub(out var client);
        var headsBefore = client.HeadInvocationCount;
        var putsBefore = client.PutInvocationCount;

        var file = (OracleObjectStorageFile)hub.Root.CreateFile("a/b/c.txt");

        Assert.Equal(headsBefore, client.HeadInvocationCount);
        Assert.Equal(putsBefore + 1, client.PutInvocationCount);
        Assert.True(file.IsLoaded);
        Assert.Equal(0, file.Length);
    }

    [Fact]
    public void OpenFile_NestedPath_CreateIfNotExists_ZeroCalls()
    {
        using var hub = NewHub(out var client);
        var headsBefore = client.HeadInvocationCount;
        var putsBefore = client.PutInvocationCount;

        var file = (OracleObjectStorageFile)hub.Root.OpenFile("a/b/c.txt", createIfNotExists: true);

        Assert.Equal(headsBefore, client.HeadInvocationCount);
        Assert.Equal(putsBefore, client.PutInvocationCount);
        Assert.False(file.IsLoaded);
    }

    [Fact]
    public void OpenDirectory_CreateIfNotExists_ZeroCalls()
    {
        using var hub = NewHub(out var client);
        var headsBefore = client.HeadInvocationCount;
        var putsBefore = client.PutInvocationCount;

        var dir = hub.Root.OpenDirectory("a/b/c", createIfNotExists: true);

        Assert.Equal(headsBefore, client.HeadInvocationCount);
        Assert.Equal(putsBefore, client.PutInvocationCount);
        Assert.NotNull(dir);
    }

    // === GetFiles — entries are unloaded (LIST has no metadata) ===

    [Fact]
    public void GetFiles_EntriesAreIsLoadedFalse()
    {
        using var hub = NewHub(out _);
        hub.Root.CreateFile("a.txt").SetText("a");
        hub.Root.CreateFile("b.txt").SetText("b");

        foreach (var entry in hub.Root.GetFiles())
            Assert.False(((OracleObjectStorageFile)entry).IsLoaded);
    }
}
