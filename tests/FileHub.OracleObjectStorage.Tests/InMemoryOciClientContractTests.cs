using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// Exercises <see cref="InMemoryOciClient"/> against the <see cref="IOciClient"/>
/// contract directly. Keeps the fake honest — if its semantics diverge from
/// what the drivers assume, these tests catch it before the higher-level
/// Directory/File/Stream tests get confused.
/// </summary>
public class InMemoryOciClientContractTests
{
    private static InMemoryOciClient NewClient() => new InMemoryOciClient();

    [Fact]
    public async Task HeadObject_Missing_ThrowsFileNotFound()
    {
        using var c = NewClient();
        await Assert.ThrowsAsync<FileNotFoundException>(() => c.HeadObjectAsync("missing"));
    }

    [Fact]
    public async Task PutThenGet_RoundTripsBody()
    {
        using var c = NewClient();
        var body = Encoding.UTF8.GetBytes("hello");
        using (var ms = new MemoryStream(body))
            await c.PutObjectAsync("k", ms, body.Length, contentType: null, opcMeta: null);

        var get = await c.GetObjectAsync("k", rangeStart: null, rangeEndInclusive: null);
        using var reader = new StreamReader(get.InputStream);
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public async Task GetObject_WithRange_ReturnsSlice()
    {
        using var c = NewClient();
        using (var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
            await c.PutObjectAsync("k", ms, 5, null, null);

        var get = await c.GetObjectAsync("k", 1, 3);
        using var ms2 = new MemoryStream();
        await get.InputStream.CopyToAsync(ms2);
        Assert.Equal(new byte[] { 2, 3, 4 }, ms2.ToArray());
    }

    [Fact]
    public async Task DeleteObject_Missing_ThrowsFileNotFound()
    {
        using var c = NewClient();
        await Assert.ThrowsAsync<FileNotFoundException>(() => c.DeleteObjectAsync("missing"));
    }

    [Fact]
    public async Task RenameObject_MovesAtomically()
    {
        using var c = NewClient();
        using (var ms = new MemoryStream(new byte[] { 9 }))
            await c.PutObjectAsync("src", ms, 1, null, null);

        await c.RenameObjectAsync("src", "dst");

        await Assert.ThrowsAsync<FileNotFoundException>(() => c.HeadObjectAsync("src"));
        var dst = await c.HeadObjectAsync("dst");
        Assert.Equal(1, dst.ContentLength);
    }

    [Fact]
    public async Task CopyObject_MissingSource_ThrowsFileNotFound()
    {
        using var c = NewClient();
        await Assert.ThrowsAsync<FileNotFoundException>(() => c.CopyObjectAsync("nope", c.Namespace, c.Bucket, c.Region, "dst"));
    }

    [Fact]
    public async Task ListObjects_WithDelimiter_ReturnsCommonPrefixes()
    {
        using var c = NewClient();
        foreach (var k in new[] { "root/", "root/a/", "root/a/x.txt", "root/a/y.txt", "root/b/z.txt", "root/c.txt" })
            using (var ms = new MemoryStream()) await c.PutObjectAsync(k, ms, 0, null, null);

        var page = await c.ListObjectsAsync("root/", "/", limit: null, start: null);
        var objects = page.Objects.Select(o => o.Name).OrderBy(n => n).ToArray();
        var prefixes = page.Prefixes.OrderBy(p => p).ToArray();

        Assert.Contains("root/", objects);          // own marker surfaces as an object
        Assert.Contains("root/c.txt", objects);
        Assert.Contains("root/a/", prefixes);
        Assert.Contains("root/b/", prefixes);
    }

    [Fact]
    public async Task ListObjects_Paginates_ViaNextStartWith()
    {
        using var c = NewClient();
        for (int i = 0; i < 10; i++)
            using (var ms = new MemoryStream()) await c.PutObjectAsync($"p/{i:D2}", ms, 0, null, null);

        var page1 = await c.ListObjectsAsync("p/", null, limit: 4, start: null);
        Assert.Equal(4, page1.Objects.Count);
        Assert.False(string.IsNullOrEmpty(page1.NextStartWith));

        var page2 = await c.ListObjectsAsync("p/", null, limit: 4, start: page1.NextStartWith);
        Assert.Equal(4, page2.Objects.Count);

        var page3 = await c.ListObjectsAsync("p/", null, limit: 4, start: page2.NextStartWith);
        Assert.Equal(2, page3.Objects.Count);
        Assert.True(string.IsNullOrEmpty(page3.NextStartWith));
    }

    [Fact]
    public async Task GetBucket_ReturnsConfiguredAccess()
    {
        using var c = NewClient();
        c.SetBucketAccess(OciBucketAccessType.ObjectReadWithoutList);
        var info = await c.GetBucketAsync();
        Assert.Equal(OciBucketAccessType.ObjectReadWithoutList, info.PublicAccessType);
    }

    [Fact]
    public async Task CreatePar_OnMissingObject_ThrowsFileNotFound()
    {
        using var c = NewClient();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => c.CreatePreauthenticatedReadRequestAsync("nope", "par", DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task CreatePar_OnExistingObject_ReturnsAccessUri()
    {
        using var c = NewClient();
        using (var ms = new MemoryStream()) await c.PutObjectAsync("k", ms, 0, null, null);
        var uri = await c.CreatePreauthenticatedReadRequestAsync("k", "parX", DateTime.UtcNow.AddMinutes(5));
        Assert.StartsWith("/p/parX/", uri);
        Assert.Single(c.Pars);
    }
}
