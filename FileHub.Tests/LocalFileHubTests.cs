using FileHub.Local;

namespace FileHub.Tests;

public class LocalFileHubTests
{
    [Fact]
    public void Constructor_WithPath_CreatesRoot()
    {
        using var temp = new TempDirectory();

        var hub = new LocalFileHub(temp.Path);

        Assert.NotNull(hub.Root);
        Assert.True(hub.Root.Exists());
    }

    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LocalFileHub(""));
    }

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LocalFileHub((string)null!));
    }

    [Fact]
    public void Constructor_TildePrefix_ResolvesUnderBaseDirectory()
    {
        var unique = "hub_" + Guid.NewGuid().ToString("N");
        try
        {
            var hub = new LocalFileHub("~/" + unique);

            var expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, unique));
            Assert.Equal(expected, hub.Root.Path);
            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            var expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, unique));
            if (Directory.Exists(expected))
                Directory.Delete(expected, recursive: true);
        }
    }

    [Fact]
    public void Constructor_WithPathResolver_UsesResolver()
    {
        using var temp = new TempDirectory();
        string? captured = null;

        var hub = new LocalFileHub("placeholder", p =>
        {
            captured = p;
            return temp.Path;
        });

        Assert.Equal("placeholder", captured);
        Assert.Equal(Path.GetFullPath(temp.Path), Path.GetFullPath(hub.Root.Path));
    }

    [Fact]
    public void Constructor_NullPathResolver_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalFileHub("x", null!));
    }

    [Fact]
    public void Root_ImplementsIFileHub()
    {
        using var temp = new TempDirectory();
        IFileHub hub = new LocalFileHub(temp.Path);
        Assert.NotNull(hub.Root);
    }
}
