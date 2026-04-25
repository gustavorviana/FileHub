using System;
using FileHub.AmazonS3.Tests.Fakes;

namespace FileHub.AmazonS3.Tests;

public class AmazonS3UrlAccessTests
{
    [Fact]
    public void GetPublicUrl_PublicBucket_ReturnsVirtualHostedUrl()
    {
        var client = new InMemoryS3Client(bucket: "my-bucket", region: "us-east-1");
        client.SetIsPublic(true);
        using var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Root.CreateFile("hello.txt").SetText("x");

        var url = ((IUrlAccessible)hub.Root.OpenFile("hello.txt")).GetPublicUrl();

        Assert.Equal("https://my-bucket.s3.us-east-1.amazonaws.com/hello.txt", url.ToString());
    }

    [Fact]
    public void GetPublicUrl_PrivateBucket_Throws()
    {
        var client = new InMemoryS3Client();
        // default IsPublic = false
        using var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Root.CreateFile("private.txt").SetText("x");

        var file = (IUrlAccessible)hub.Root.OpenFile("private.txt");

        Assert.Throws<InvalidOperationException>(() => file.GetPublicUrl());
    }

    [Fact]
    public void GetSignedUrl_ReturnsPresignedUri()
    {
        var client = new InMemoryS3Client(bucket: "my-bucket", region: "us-east-1");
        using var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Root.CreateFile("signed.txt").SetText("x");

        var url = ((IUrlAccessible)hub.Root.OpenFile("signed.txt")).GetSignedUrl(TimeSpan.FromMinutes(15));

        Assert.StartsWith("https://my-bucket.s3.us-east-1.amazonaws.com/signed.txt", url.ToString());
        Assert.Contains("X-Amz-Signature=", url.ToString());
    }

    [Fact]
    public void GetSignedUrl_NonPositiveExpires_Throws()
    {
        var client = new InMemoryS3Client();
        using var hub = AmazonS3FileHub.FromS3Client(client);
        hub.Root.CreateFile("x.txt").SetText(".");
        var file = (IUrlAccessible)hub.Root.OpenFile("x.txt");

        Assert.Throws<ArgumentOutOfRangeException>(() => file.GetSignedUrl(TimeSpan.Zero));
    }
}
