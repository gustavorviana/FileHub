using System;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

public class OracleObjectStorageUrlAccessTests : IClassFixture<InMemoryOciFixture>
{
    private readonly InMemoryOciFixture _fixture;
    private FileDirectory Root => _fixture.FileHub.Root;

    public OracleObjectStorageUrlAccessTests(InMemoryOciFixture fixture) => _fixture = fixture;

    [Fact]
    public void IsPublic_ReflectsFakeBucketAccess()
    {
        using var fake = new InMemoryOciClient();
        fake.SetBucketAccess(OciBucketAccessType.ObjectRead);
        using var hub = OracleObjectStorageFileHub.FromOciClient(fake);

        var file = (OracleObjectStorageFile)hub.Root.CreateFile("public.txt");
        Assert.True(file.IsPublic);
    }

    [Fact]
    public void GetPublicUrl_OnPublicBucket_ReturnsConstructedUrl()
    {
        using var fake = new InMemoryOciClient(bucket: "my-bucket", @namespace: "my-ns", region: "us-phoenix-1");
        fake.SetBucketAccess(OciBucketAccessType.ObjectRead);
        using var hub = OracleObjectStorageFileHub.FromOciClient(fake);

        var file = (OracleObjectStorageFile)hub.Root.CreateFile("pub.txt");
        var url = file.GetPublicUrl();

        Assert.Contains("objectstorage.us-phoenix-1.oraclecloud.com", url.ToString());
        Assert.Contains("/n/my-ns/", url.ToString());
        Assert.Contains("/b/my-bucket/", url.ToString());
        Assert.Contains("pub.txt", url.ToString());
    }

    [Fact]
    public void GetPublicUrl_OnPrivateBucket_Throws()
    {
        // default fixture fake is NoPublicAccess
        var file = (OracleObjectStorageFile)Root.CreateFile("priv.txt");
        Assert.Throws<InvalidOperationException>(() => file.GetPublicUrl());
    }

    [Fact]
    public async Task GetSignedUrlAsync_ReturnsUrlWithAccessUri()
    {
        var file = (OracleObjectStorageFile)Root.CreateFile("signed.txt");
        file.SetText("signed-content");

        var url = await file.GetSignedUrlAsync(TimeSpan.FromMinutes(5));

        // The fake produces /p/{parName}/n/{ns}/b/{bucket}/o/{encoded}
        Assert.Contains("/p/signed.txt", url.ToString());
        Assert.Contains("/n/test-ns/", url.ToString());
        Assert.Contains("/b/test-bucket/", url.ToString());
        Assert.Single(_fixture.Client.Pars.Where(p => p.ObjectName == "signed.txt"));
    }

    [Fact]
    public void GetSignedUrl_ExpiresInMustBePositive()
    {
        var file = (OracleObjectStorageFile)Root.CreateFile("bad.txt");
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSignedUrl(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSignedUrl(TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public async Task GetSignedUrlAsync_CancelsWhenTokenCanceled()
    {
        var file = (OracleObjectStorageFile)Root.CreateFile("cancel.txt");
        file.SetText("x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => file.GetSignedUrlAsync(TimeSpan.FromMinutes(1), cts.Token));
    }

    [Fact]
    public async Task GetSignedUploadUrlAsync_OnDirectory_CreatesWritePar_NoObjectNeeded()
    {
        // The directory mints the URL; the target object never has to exist
        // (no CreateFile call below). Caller PUTs straight to the access URI.
        var dir = (ISignedUploadable)Root;

        var url = await dir.GetSignedUploadUrlAsync("upload.bin", TimeSpan.FromMinutes(5));

        Assert.Contains("/p/filehub-upload-", url.ToString());
        Assert.Contains("/n/test-ns/", url.ToString());
        Assert.Contains("/b/test-bucket/", url.ToString());
        Assert.Single(_fixture.Client.Pars.Where(p => p.ObjectName == "upload.bin" && p.Name.StartsWith("filehub-upload-")));
        Assert.False(Root.FileExists("upload.bin"));
    }

    [Fact]
    public void GetSignedUploadUrl_ExpiresInMustBePositive()
    {
        var dir = (ISignedUploadable)Root;
        Assert.Throws<ArgumentOutOfRangeException>(() => dir.GetSignedUploadUrl("bad-upload.bin", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => dir.GetSignedUploadUrl("bad-upload.bin", TimeSpan.FromMinutes(-1)));
    }
}
