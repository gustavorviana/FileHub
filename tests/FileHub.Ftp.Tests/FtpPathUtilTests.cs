namespace FileHub.Ftp.Tests;

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
    [InlineData("/", "")]
    [InlineData("/file.txt", "file.txt")]
    [InlineData("/a/b/c", "c")]
    [InlineData("/a/b/c/", "c")]
    public void GetLeafName_ReturnsLastSegment(string path, string expected)
    {
        Assert.Equal(expected, FtpPathUtil.GetLeafName(path));
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

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void ValidateName_RejectsInvalid(string name)
    {
        Assert.Throws<ArgumentException>(() => FtpPathUtil.ValidateName(name));
    }

    [Fact]
    public void ValidateName_Null_Rejected()
    {
        Assert.Throws<ArgumentException>(() => FtpPathUtil.ValidateName(null!));
    }

    [Fact]
    public void ValidateName_AcceptsLeafName()
    {
        FtpPathUtil.ValidateName("report-2026.csv");
    }

    [Fact]
    public void BuildSearchPatternRegex_StarMatchesEverything()
    {
        var rx = FtpPathUtil.BuildSearchPatternRegex("*");
        Assert.Matches(rx, "anything");
    }

    [Fact]
    public void BuildSearchPatternRegex_HonoursStarSegment()
    {
        var rx = FtpPathUtil.BuildSearchPatternRegex("*.csv");
        Assert.Matches(rx, "data.csv");
        Assert.DoesNotMatch(rx, "data.txt");
    }
}
