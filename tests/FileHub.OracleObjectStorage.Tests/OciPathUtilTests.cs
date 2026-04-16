using System;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage.Tests;

public class OciPathUtilTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("uploads", "uploads/")]
    [InlineData("/uploads", "uploads/")]
    [InlineData("/uploads/", "uploads/")]
    [InlineData("uploads/2026/", "uploads/2026/")]
    [InlineData("\\uploads", "uploads/")]
    public void NormalizePrefix_ProducesCanonicalForm(string? input, string expected)
        => Assert.Equal(expected, OciPathUtil.NormalizePrefix(input!));

    [Theory]
    [InlineData("", "/")]
    [InlineData("uploads/", "/uploads")]
    [InlineData("uploads/2026/", "/uploads/2026")]
    public void DisplayPath_IsFilesystemLike(string prefix, string expected)
        => Assert.Equal(expected, OciPathUtil.DisplayPath(prefix));

    [Theory]
    [InlineData("uploads/", "file.txt", "uploads/file.txt")]
    [InlineData("", "file.txt", "file.txt")]
    public void CombineObjectName_Concatenates(string prefix, string file, string expected)
        => Assert.Equal(expected, OciPathUtil.CombineObjectName(prefix, file));

    [Theory]
    [InlineData("uploads/", "2026", "uploads/2026/")]
    [InlineData("", "root", "root/")]
    public void CombinePrefix_AppendsTrailingSlash(string parent, string child, string expected)
        => Assert.Equal(expected, OciPathUtil.CombinePrefix(parent, child));

    [Theory]
    [InlineData("uploads/", "uploads")]
    [InlineData("a/b/c/", "c")]
    [InlineData("leaf", "leaf")]
    [InlineData("", "")]
    public void GetLeafName_ExtractsLastSegment(string prefix, string expected)
        => Assert.Equal(expected, OciPathUtil.GetLeafName(prefix));

    [Theory]
    [InlineData("*")]
    [InlineData("*.*")]
    public void BuildSearchPatternRegex_Wildcard_MatchesAll(string pattern)
    {
        var regex = OciPathUtil.BuildSearchPatternRegex(pattern);
        Assert.Matches(regex, "anything.txt");
        Assert.Matches(regex, "");
    }

    [Fact]
    public void BuildSearchPatternRegex_Extension_MatchesOnlyExact()
    {
        var regex = OciPathUtil.BuildSearchPatternRegex("*.txt");
        Assert.Matches(regex, "file.txt");
        Assert.DoesNotMatch(regex, "file.log");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    public void ValidateName_Rejects_InvalidNames(string? name)
        => Assert.Throws<ArgumentException>(() => OciPathUtil.ValidateName(name!));

    [Fact]
    public void EnsureWithinRootPrefix_AllowsEmptyRoot()
    {
        // empty root => bucket-wide access, no containment check
        OciPathUtil.EnsureWithinRootPrefix("", "anywhere/inside.txt");
    }

    [Fact]
    public void EnsureWithinRootPrefix_BlocksEscape()
    {
        Assert.Throws<FileHubException>(
            () => OciPathUtil.EnsureWithinRootPrefix("uploads/", "not-uploads/file.txt"));
    }

    [Fact]
    public void ResolveSafeObjectName_AppliesContainment()
    {
        Assert.Throws<FileHubException>(
            () => OciPathUtil.ResolveSafeObjectName("uploads/", "other/", "file.txt"));

        var ok = OciPathUtil.ResolveSafeObjectName("uploads/", "uploads/2026/", "file.txt");
        Assert.Equal("uploads/2026/file.txt", ok);
    }
}
