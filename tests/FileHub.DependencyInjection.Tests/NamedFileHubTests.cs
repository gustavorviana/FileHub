using FileHub.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection.Tests;

public class NamedFileHubTests
{
    [Fact]
    public void AddNamedFileHubs_RegistersRegistryAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(builder =>
        {
            builder.Register("uploads", new MemoryFileHub("uploads-root"));
        });

        using var provider = services.BuildServiceProvider();
        var r1 = provider.GetRequiredService<INamedFileHubs>();
        var r2 = provider.GetRequiredService<INamedFileHubs>();

        Assert.Same(r1, r2);
    }

    [Fact]
    public void AddNamedFileHubs_ResolvesByName()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(builder =>
        {
            builder.Register("uploads", new MemoryFileHub("uploads-root"));
            builder.Register("logs",    new MemoryFileHub("logs-root"));
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        var uploads = registry.GetByName("uploads");
        var logs    = registry.GetByName("logs");

        Assert.NotNull(uploads);
        Assert.NotNull(logs);
        Assert.NotSame(uploads, logs);
        Assert.Equal("uploads-root", uploads.Root.Name);
        Assert.Equal("logs-root",    logs.Root.Name);
    }

    [Fact]
    public void AddNamedFileHubs_DoesNotRegisterKeyedServices()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(builder =>
        {
            builder.Register("uploads", new MemoryFileHub());
        });

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<IFileHub>("uploads"));
        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<IMemoryFileHub>("uploads"));
    }

    [Fact]
    public void AddNamedFileHubs_WithServiceProvider_AccessesResolvedServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StorageConfig { Root = "custom-root" });

        services.AddNamedFileHubs((sp, builder) =>
        {
            var cfg = sp.GetRequiredService<StorageConfig>();
            builder.Register("uploads", new MemoryFileHub(cfg.Root));
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        Assert.Equal("custom-root", registry.GetRootByName("uploads").Name);
    }

    [Fact]
    public void GetByName_UnknownName_ReturnsNull()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(b => b.Register("uploads", new MemoryFileHub()));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        Assert.Null(registry.GetByName("nope"));
        Assert.Null(registry.GetRootByName("nope"));
    }

    [Fact]
    public void GetByName_NullOrEmpty_ReturnsNull()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(b => b.Register("uploads", new MemoryFileHub()));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        Assert.Null(registry.GetByName(null));
        Assert.Null(registry.GetByName(""));
        Assert.Null(registry.GetRootByName(null));
        Assert.Null(registry.GetRootByName(""));
    }

    [Fact]
    public void GetRootByName_ReturnsHubRoot()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(b => b.Register("uploads", new MemoryFileHub("uploads-root")));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        var root = registry.GetRootByName("uploads");

        Assert.NotNull(root);
        Assert.Equal("uploads-root", root.Name);
    }

    [Fact]
    public void GetByName_IsCaseInsensitive()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(b => b.Register("Uploads", new MemoryFileHub()));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        Assert.NotNull(registry.GetByName("uploads"));
        Assert.NotNull(registry.GetByName("UPLOADS"));
        Assert.NotNull(registry.GetByName("Uploads"));
    }

    [Fact]
    public void Builder_Register_SameNameTwice_OverwritesEarlier()
    {
        var first  = new MemoryFileHub("first");
        var second = new MemoryFileHub("second");

        var services = new ServiceCollection();
        services.AddNamedFileHubs(b =>
        {
            b.Register("dup", first);
            b.Register("dup", second);
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        Assert.Same(second, registry.GetByName("dup"));
    }

    [Fact]
    public void AddNamedFileHubs_CoexistsWithAddFileHub()
    {
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(sp => new MemoryFileHub("default-root"));
        services.AddNamedFileHubs(b => b.Register("scratch", new MemoryFileHub("scratch-root")));

        using var provider = services.BuildServiceProvider();
        var def      = provider.GetRequiredService<IMemoryFileHub>();
        var registry = provider.GetRequiredService<INamedFileHubs>();
        var scratch  = registry.GetByName("scratch");

        Assert.NotNull(scratch);
        Assert.NotSame(def, scratch);
        Assert.Equal("default-root", def.Root.Name);
        Assert.Equal("scratch-root", scratch.Root.Name);
    }

    [Fact]
    public void AddNamedFileHubs_ConfigureNull_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => services.AddNamedFileHubs((Action<NamedFileHubsBuilder>)null!));
        Assert.Throws<ArgumentNullException>(
            () => services.AddNamedFileHubs((Action<IServiceProvider, NamedFileHubsBuilder>)null!));
    }

    [Fact]
    public void Builder_Build_ProducesIndependentRegistry_MutationAfterBuildHasNoEffect()
    {
        var builder = new NamedFileHubsBuilder()
            .Register("a", new MemoryFileHub("a-root"));

        var registry = builder.Build();

        // Mutating the builder after Build() must not affect the already-built registry.
        builder.Register("b", new MemoryFileHub("b-root"));

        Assert.NotNull(registry.GetByName("a"));
        Assert.Null(registry.GetByName("b"));
    }

    [Fact]
    public void Builder_Standalone_ProducesWorkingRegistry()
    {
        var hubs = new NamedFileHubsBuilder()
            .Register("reports", new MemoryFileHub("reports-root"))
            .Register("logs",    new MemoryFileHub("logs-root"))
            .Build();

        Assert.Equal("reports-root", hubs.GetRootByName("reports").Name);
        Assert.Equal("logs-root",    hubs.GetRootByName("logs").Name);
        Assert.Null(hubs.GetByName("nope"));
    }

    [Fact]
    public void Builder_RegisterNullHub_Throws()
    {
        var builder = new NamedFileHubsBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.Register("a", null));
    }

    [Fact]
    public void Builder_RegisterEmptyName_Throws()
    {
        var builder = new NamedFileHubsBuilder();
        Assert.Throws<ArgumentException>(() => builder.Register("", new MemoryFileHub()));
        Assert.Throws<ArgumentException>(() => builder.Register(null, new MemoryFileHub()));
    }

    private sealed class StorageConfig
    {
        public string Root { get; set; } = "";
    }
}
