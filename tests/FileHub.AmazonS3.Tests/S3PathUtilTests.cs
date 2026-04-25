using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3.Tests;

public class S3PathUtilTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("foo", "foo/")]
    [InlineData("foo/", "foo/")]
    [InlineData("/foo", "foo/")]
    [InlineData("/foo/bar", "foo/bar/")]
    [InlineData(@"foo\bar", "foo/bar/")]
    public void NormalizePrefix_VariousInputs(string? input, string expected)
    {
        Assert.Equal(expected, S3PathUtil.NormalizePrefix(input!));
    }

    [Fact]
    public void CombineObjectKey_PrependsPrefix()
    {
        Assert.Equal("folder/file.txt", S3PathUtil.CombineObjectKey("folder/", "file.txt"));
        Assert.Equal("file.txt", S3PathUtil.CombineObjectKey(null!, "file.txt"));
        Assert.Equal("file.txt", S3PathUtil.CombineObjectKey("", "file.txt"));
    }

    [Fact]
    public void CombinePrefix_AppendsTrailingSlash()
    {
        Assert.Equal("a/b/", S3PathUtil.CombinePrefix("a/", "b"));
        Assert.Equal("b/", S3PathUtil.CombinePrefix(null!, "b"));
    }

    [Fact]
    public void GetLeafName_StripsTrailingSlash()
    {
        Assert.Equal("bar", S3PathUtil.GetLeafName("foo/bar/"));
        Assert.Equal("bar", S3PathUtil.GetLeafName("foo/bar"));
        Assert.Equal("", S3PathUtil.GetLeafName(""));
    }

    [Fact]
    public void DisplayPath_RootMapsToSlash()
    {
        Assert.Equal("/", S3PathUtil.DisplayPath(""));
        Assert.Equal("/foo", S3PathUtil.DisplayPath("foo/"));
        Assert.Equal("/foo/bar", S3PathUtil.DisplayPath("foo/bar/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("\t")]
    public void ValidateName_RejectsBadNames(string name)
    {
        Assert.Throws<System.ArgumentException>(() => S3PathUtil.ValidateName(name));
    }

    [Fact]
    public void EnsureWithinRootPrefix_AllowsSubpaths()
    {
        S3PathUtil.EnsureWithinRootPrefix("tenant/", "tenant/sub/file.txt");
        S3PathUtil.EnsureWithinRootPrefix("", "any/file.txt");
    }

    [Fact]
    public void EnsureWithinRootPrefix_RejectsEscape()
    {
        Assert.Throws<FileHubException>(() =>
            S3PathUtil.EnsureWithinRootPrefix("tenant/", "other/file.txt"));
    }
}
