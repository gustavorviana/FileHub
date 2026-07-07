namespace FileHub.Tests;

/// <summary>
/// Tests for the path/name rules shared by every driver. Driver test suites
/// only cover what remains driver-specific (e.g. FtpPathUtil's absolute-path
/// model).
/// </summary>
public class PathUtilTests
{
    // === ValidateName (portable) ===

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("\t")]
    [InlineData("a\nb")]
    public void ValidateName_RejectsBadNames(string? name)
    {
        Assert.Throws<ArgumentException>(() => PathUtil.ValidateName(name));
    }

    [Theory]
    [InlineData("report-2026.csv")]
    [InlineData("a<b>.txt")] // portable rule: OS-specific chars are legal
    public void ValidateName_AcceptsPortableNames(string name)
    {
        PathUtil.ValidateName(name);
    }

    [Fact]
    public void ValidateLocalName_AddsOsInvalidChars()
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars()
            .FirstOrDefault(c => c != '/' && c != '\\' && !char.IsControl(c));
        if (invalid == default) return; // Linux/macOS: nothing beyond the portable set

        Assert.Throws<ArgumentException>(() => PathUtil.ValidateLocalName($"a{invalid}b.txt"));
        PathUtil.ValidateLocalName("plain.txt");
    }

    // === SplitAndValidateSegments ===

    [Theory]
    [InlineData("a/b/c", new[] { "a", "b", "c" })]
    [InlineData("a\\b", new[] { "a", "b" })]
    [InlineData("a/b/", new[] { "a", "b" })]
    [InlineData("single", new[] { "single" })]
    public void SplitAndValidateSegments_SplitsOnBothSeparators(string input, string[] expected)
    {
        Assert.Equal(expected, PathUtil.SplitAndValidateSegments(input));
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("./a")]
    public void SplitAndValidateSegments_RejectsTraversal(string input)
    {
        Assert.Throws<FileHubException>(() => PathUtil.SplitAndValidateSegments(input));
    }

    [Theory]
    [InlineData("/a/b")]
    [InlineData("\\a")]
    [InlineData("/")]
    public void SplitAndValidateSegments_RejectsAbsolute(string input)
    {
        Assert.Throws<FileHubException>(() => PathUtil.SplitAndValidateSegments(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SplitAndValidateSegments_RejectsEmpty(string input)
    {
        Assert.Throws<ArgumentException>(() => PathUtil.SplitAndValidateSegments(input));
    }

    [Fact]
    public void SplitAndValidateSegments_UsesCallerValidator()
    {
        var seen = new List<string>();
        PathUtil.SplitAndValidateSegments("a/b", seen.Add);
        Assert.Equal(new[] { "a", "b" }, seen);
    }

    // === Glob matching ===

    [Theory]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("")]
    public void BuildSearchPatternRegex_Wildcard_MatchesAll(string pattern)
    {
        var regex = PathUtil.BuildSearchPatternRegex(pattern);
        Assert.Matches(regex, "anything.txt");
    }

    [Fact]
    public void BuildSearchPatternRegex_Extension_MatchesOnlyExact()
    {
        var regex = PathUtil.BuildSearchPatternRegex("*.txt");
        Assert.Matches(regex, "file.txt");
        Assert.DoesNotMatch(regex, "file.log");
    }

    [Fact]
    public void BuildSearchPatternRegex_QuestionMark_MatchesSingleChar()
    {
        var regex = PathUtil.BuildSearchPatternRegex("report_?.csv");
        Assert.Matches(regex, "report_1.csv");
        Assert.DoesNotMatch(regex, "report_12.csv");
    }

    // === Leaf / prefix helpers (object-storage model) ===

    [Theory]
    [InlineData("foo/bar/", "bar")]
    [InlineData("foo/bar", "bar")]
    [InlineData("leaf", "leaf")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void GetLeafName_StripsTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, PathUtil.GetLeafName(input));
    }

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
        Assert.Equal(expected, PathUtil.NormalizePrefix(input));
    }

    [Fact]
    public void CombineKey_PrependsPrefix()
    {
        Assert.Equal("folder/file.txt", PathUtil.CombineKey("folder/", "file.txt"));
        Assert.Equal("file.txt", PathUtil.CombineKey(null, "file.txt"));
        Assert.Equal("file.txt", PathUtil.CombineKey("", "file.txt"));
    }

    [Fact]
    public void CombinePrefix_AppendsTrailingSlash()
    {
        Assert.Equal("a/b/", PathUtil.CombinePrefix("a/", "b"));
        Assert.Equal("b/", PathUtil.CombinePrefix(null, "b"));
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("foo/", "/foo")]
    [InlineData("foo/bar/", "/foo/bar")]
    public void DisplayPath_RootMapsToSlash(string prefix, string expected)
    {
        Assert.Equal(expected, PathUtil.DisplayPath(prefix));
    }

    // === Root confinement ===

    [Fact]
    public void EnsureWithinRootPrefix_AllowsSubpathsAndEmptyRoot()
    {
        PathUtil.EnsureWithinRootPrefix("tenant/", "tenant/sub/file.txt");
        PathUtil.EnsureWithinRootPrefix("", "any/file.txt");
    }

    [Fact]
    public void EnsureWithinRootPrefix_RejectsEscape()
    {
        Assert.Throws<FileHubException>(() =>
            PathUtil.EnsureWithinRootPrefix("tenant/", "other/file.txt"));
    }

    [Fact]
    public void EnsureWithinRootPrefix_RejectsPrefixCollision()
    {
        // Root without trailing slash must not be escaped by a sibling
        // prefix that merely starts with the same characters.
        Assert.Throws<FileHubException>(() =>
            PathUtil.EnsureWithinRootPrefix("tenant", "tenant2/file.txt"));

        PathUtil.EnsureWithinRootPrefix("tenant", "tenant/file.txt");
        PathUtil.EnsureWithinRootPrefix("tenant", "tenant"); // the root itself
    }

    [Fact]
    public void ResolveSafeKey_AppliesContainment()
    {
        Assert.Throws<FileHubException>(() =>
            PathUtil.ResolveSafeKey("uploads/", "other/", "file.txt"));

        Assert.Equal("uploads/2026/file.txt", PathUtil.ResolveSafeKey("uploads/", "uploads/2026/", "file.txt"));
    }

    [Fact]
    public void ResolveSafeChildPrefix_AppendsSlashAndAppliesContainment()
    {
        Assert.Equal("uploads/2026/", PathUtil.ResolveSafeChildPrefix("uploads/", "uploads/", "2026"));

        Assert.Throws<FileHubException>(() =>
            PathUtil.ResolveSafeChildPrefix("uploads/", "other/", "2026"));
    }
}
