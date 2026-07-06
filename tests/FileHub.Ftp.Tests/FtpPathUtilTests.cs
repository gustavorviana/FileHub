namespace FileHub.Ftp.Tests;

/// <summary>
/// Covers only what is specific to the FTP path model. Name validation,
/// glob matching and leaf extraction are shared via PathUtil and covered
/// by the core suite.
/// </summary>
public class FtpPathUtilTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("uploads", "/uploads")]
    [InlineData("/uploads", "/uploads")]
    [InlineData("uploads/", "/uploads")]
    [InlineData("/uploads/2026/", "/uploads/2026")]
    [InlineData("\\uploads\\2026", "/uploads/2026")]
    public void NormalizeRoot_ProducesAbsoluteWithoutTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, FtpPathUtil.NormalizeRoot(input));
    }

    [Fact]
    public void NormalizeRoot_Null_ReturnsServerRoot()
    {
        Assert.Equal("/", FtpPathUtil.NormalizeRoot(null!));
    }

    [Theory]
    [InlineData("/", "file.txt", "/file.txt")]
    [InlineData("/uploads", "file.txt", "/uploads/file.txt")]
    [InlineData("/uploads/2026", "file.txt", "/uploads/2026/file.txt")]
    public void Combine_JoinsWithSingleSlash(string parent, string child, string expected)
    {
        Assert.Equal(expected, FtpPathUtil.Combine(parent, child));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/file.txt", "/")]
    [InlineData("/a/b/c", "/a/b")]
    public void GetParent_ReturnsContainingDirectory(string path, string expected)
    {
        Assert.Equal(expected, FtpPathUtil.GetParent(path));
    }

    [Fact]
    public void EnsureWithinRoot_AllowsRootItself()
    {
        FtpPathUtil.EnsureWithinRoot("/uploads", "/uploads");
    }

    [Fact]
    public void EnsureWithinRoot_AllowsNestedChildren()
    {
        FtpPathUtil.EnsureWithinRoot("/uploads", "/uploads/2026/x.txt");
    }

    [Fact]
    public void EnsureWithinRoot_RejectsSiblingPath()
    {
        Assert.Throws<FileHubException>(() => FtpPathUtil.EnsureWithinRoot("/uploads", "/other/x.txt"));
    }

    [Fact]
    public void EnsureWithinRoot_RejectsRootPrefixWithoutSeparator()
    {
        Assert.Throws<FileHubException>(() => FtpPathUtil.EnsureWithinRoot("/uploads", "/uploadsX/x.txt"));
    }

    [Fact]
    public void EnsureWithinRoot_RootSlash_AcceptsAnything()
    {
        FtpPathUtil.EnsureWithinRoot("/", "/anywhere/at/all");
    }

    [Fact]
    public void ResolveSafeChildPath_ValidatesAndConfines()
    {
        Assert.Equal("/uploads/file.txt", FtpPathUtil.ResolveSafeChildPath("/uploads", "/uploads", "file.txt"));
        Assert.Throws<ArgumentException>(() => FtpPathUtil.ResolveSafeChildPath("/uploads", "/uploads", "a/b"));
    }
}
