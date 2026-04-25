using FileHub.Memory;

namespace FileHub.Tests;

public class MemoryFileHubTests
{
    [Fact]
    public void DefaultConstructor_CreatesRootNamedRoot()
    {
        var hub = new MemoryFileHub();

        Assert.NotNull(hub.Root);
        Assert.Equal("root", hub.Root.Name);
        Assert.True(hub.Root.Exists());
    }

    [Fact]
    public void CustomRootName_UsesProvidedName()
    {
        var hub = new MemoryFileHub("workspace");

        Assert.Equal("workspace", hub.Root.Name);
        Assert.Equal("workspace", hub.Root.Path);
    }

    [Fact]
    public void Root_IsAMemoryDirectory()
    {
        var hub = new MemoryFileHub();
        Assert.IsType<MemoryDirectory>(hub.Root);
    }

    [Fact]
    public void Root_ImplementsIFileHub()
    {
        IFileHub hub = new MemoryFileHub();
        Assert.NotNull(hub.Root);
    }
}
