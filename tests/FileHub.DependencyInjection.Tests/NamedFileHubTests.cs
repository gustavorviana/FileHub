using FileHub.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection.Tests;

public class NamedFileHubTests
{
    [Fact]
    public void AddNamedFileHubs_ResolvesRegistryFromProvider()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(builder =>
        {
            builder.Register("uploads", new MemoryFileHub("uploads-root"));
        });

        using var provider = services.BuildServiceProvider();
        var r1 = provider.GetRequiredService<INamedFileHubs>();
        var r2 = provider.GetRequiredService<INamedFileHubs>();

        // Same root-scope provider returns the same scoped registry instance.
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
            () => services.AddNamedFileHubs((Action<NamedFileHubsServiceBuilder>)null!));
        Assert.Throws<ArgumentNullException>(
            () => services.AddNamedFileHubs((Action<IServiceProvider, NamedFileHubsServiceBuilder>)null!));
    }

    [Fact]
    public void Builder_Standalone_ProducesWorkingRegistry()
    {
        // Core NamedFileHubsBuilder remains available for non-DI usage.
        var hubs = new NamedFileHubsBuilder()
            .Register("reports", new MemoryFileHub("reports-root"))
            .Register("logs",    new MemoryFileHub("logs-root"))
            .Build();

        Assert.Equal("reports-root", hubs.GetRootByName("reports").Name);
        Assert.Equal("logs-root",    hubs.GetRootByName("logs").Name);
        Assert.Null(hubs.GetByName("nope"));
    }

    [Fact]
    public void Builder_Build_ProducesIndependentRegistry_MutationAfterBuildHasNoEffect()
    {
        var builder = new NamedFileHubsBuilder()
            .Register("a", new MemoryFileHub("a-root"));

        var registry = builder.Build();

        builder.Register("b", new MemoryFileHub("b-root"));

        Assert.NotNull(registry.GetByName("a"));
        Assert.Null(registry.GetByName("b"));
    }

    [Fact]
    public void ServiceBuilder_RegisterNullHub_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddNamedFileHubs(b => b.Register("a", (IFileHub)null!)));
    }

    [Fact]
    public void ServiceBuilder_RegisterNullFactory_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => services.AddNamedFileHubs(b => b.Register("a", (Func<IServiceProvider, IFileHub>)null!)));
    }

    [Fact]
    public void ServiceBuilder_RegisterEmptyName_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddNamedFileHubs(b => b.Register("", new MemoryFileHub())));
        Assert.Throws<ArgumentException>(() => services.AddNamedFileHubs(b => b.Register(null!, new MemoryFileHub())));
        Assert.Throws<ArgumentException>(
            () => services.AddNamedFileHubs(b => b.Register("", _ => new MemoryFileHub())));
    }

    [Fact]
    public void Register_Factory_SingletonLifetime_CachesAcrossScopes()
    {
        var services = new ServiceCollection();
        var calls = 0;
        services.AddNamedFileHubs(b => b.Register(
            "cached",
            _ => { calls++; return new MemoryFileHub($"hub-{calls}"); },
            ServiceLifetime.Singleton));

        using var provider = services.BuildServiceProvider();

        IFileHub a, b2;
        using (var scope = provider.CreateScope())
            a = scope.ServiceProvider.GetRequiredService<INamedFileHubs>().GetByName("cached");
        using (var scope = provider.CreateScope())
            b2 = scope.ServiceProvider.GetRequiredService<INamedFileHubs>().GetByName("cached");

        Assert.Same(a, b2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Register_Factory_ScopedLifetime_NewInstancePerScope_SharedWithinScope()
    {
        var services = new ServiceCollection();
        var calls = 0;
        services.AddNamedFileHubs(b => b.Register(
            "per-scope",
            _ => { calls++; return new MemoryFileHub($"hub-{calls}"); },
            ServiceLifetime.Scoped));

        using var provider = services.BuildServiceProvider();

        IFileHub a1, a2, b1;
        using (var scope = provider.CreateScope())
        {
            var reg = scope.ServiceProvider.GetRequiredService<INamedFileHubs>();
            a1 = reg.GetByName("per-scope");
            a2 = reg.GetByName("per-scope");
        }
        using (var scope = provider.CreateScope())
        {
            b1 = scope.ServiceProvider.GetRequiredService<INamedFileHubs>().GetByName("per-scope");
        }

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b1);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Register_Factory_TransientLifetime_NewInstancePerResolve()
    {
        var services = new ServiceCollection();
        var calls = 0;
        services.AddNamedFileHubs(b => b.Register(
            "transient",
            _ => { calls++; return new MemoryFileHub($"hub-{calls}"); },
            ServiceLifetime.Transient));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        var a = registry.GetByName("transient");
        var b2 = registry.GetByName("transient");

        Assert.NotSame(a, b2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Register_Factory_ScopedLifetime_ResolvesPerScopeDependency()
    {
        // Simulates tenant-aware resolution: a scoped ITenantContext drives the
        // hub's root path, so each scope gets its own hub.
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddNamedFileHubs(b => b.Register(
            "tenant",
            sp => new MemoryFileHub(sp.GetRequiredService<ITenantContext>().Id),
            ServiceLifetime.Scoped));

        using var provider = services.BuildServiceProvider();

        string scopeAId, scopeBId;
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Id = "tenant-a";
            scopeAId = scope.ServiceProvider.GetRequiredService<INamedFileHubs>()
                .GetRootByName("tenant").Name;
        }
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Id = "tenant-b";
            scopeBId = scope.ServiceProvider.GetRequiredService<INamedFileHubs>()
                .GetRootByName("tenant").Name;
        }

        Assert.Equal("tenant-a", scopeAId);
        Assert.Equal("tenant-b", scopeBId);
    }

    [Fact]
    public void Register_Factory_ScopedHub_DisposedWhenScopeEnds()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(b => b.Register(
            "scoped",
            _ => new DisposableFileHub(),
            ServiceLifetime.Scoped));

        using var provider = services.BuildServiceProvider();

        DisposableFileHub hub;
        using (var scope = provider.CreateScope())
        {
            hub = (DisposableFileHub)scope.ServiceProvider
                .GetRequiredService<INamedFileHubs>().GetByName("scoped");
            Assert.False(hub.Disposed);
        }

        Assert.True(hub.Disposed);
    }

    [Fact]
    public void Register_Factory_SingletonHub_DisposedWhenContainerDisposed()
    {
        var services = new ServiceCollection();
        services.AddNamedFileHubs(b => b.Register(
            "singleton",
            _ => new DisposableFileHub(),
            ServiceLifetime.Singleton));

        var provider = services.BuildServiceProvider();
        var hub = (DisposableFileHub)provider.GetRequiredService<INamedFileHubs>().GetByName("singleton");

        Assert.False(hub.Disposed);
        provider.Dispose();
        Assert.True(hub.Disposed);
    }

    [Fact]
    public void Register_Factory_DefaultLifetime_IsSingleton()
    {
        var services = new ServiceCollection();
        var calls = 0;
        services.AddNamedFileHubs(b => b.Register(
            "default",
            _ => { calls++; return new MemoryFileHub(); }));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<INamedFileHubs>();

        var a = registry.GetByName("default");
        var b2 = registry.GetByName("default");

        Assert.Same(a, b2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AddNamedFileHubs_WithServiceProvider_SupportsFactoryLifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StorageConfig { Root = "base" });
        services.AddScoped<ITenantContext, TenantContext>();

        services.AddNamedFileHubs((sp, builder) =>
        {
            var cfg = sp.GetRequiredService<StorageConfig>();
            builder.Register(
                "tenant",
                innerSp => new MemoryFileHub($"{cfg.Root}/{innerSp.GetRequiredService<ITenantContext>().Id}"),
                ServiceLifetime.Scoped);
        });

        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Id = "acme";
        var rootName = scope.ServiceProvider.GetRequiredService<INamedFileHubs>()
            .GetRootByName("tenant").Name;

        Assert.Equal("base/acme", rootName);
    }

    private sealed class StorageConfig
    {
        public string Root { get; set; } = "";
    }

    private interface ITenantContext
    {
        string Id { get; set; }
    }

    private sealed class TenantContext : ITenantContext
    {
        public string Id { get; set; } = "";
    }

    private sealed class DisposableFileHub : IFileHub, IDisposable
    {
        private readonly MemoryFileHub _inner = new();
        public FileDirectory Root => _inner.Root;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
