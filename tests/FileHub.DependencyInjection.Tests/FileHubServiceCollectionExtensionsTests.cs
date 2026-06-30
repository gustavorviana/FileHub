using FileHub.DependencyInjection;
using FileHub.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection.Tests;

public class FileHubServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFileHub_Factory_RegistersTypedInterfaceAndIFileHub()
    {
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(sp => new MemoryFileHub("root-a"));

        using var provider = services.BuildServiceProvider();
        var typed = provider.GetRequiredService<IMemoryFileHub>();
        var generic = provider.GetRequiredService<IFileHub>();

        Assert.Same(typed, generic);
    }

    [Fact]
    public void AddFileHub_Instance_RegistersAsSingleton()
    {
        var instance = new MemoryFileHub("prebuilt");
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(instance);

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IMemoryFileHub>();
        var b = provider.GetRequiredService<IMemoryFileHub>();
        var asGeneric = provider.GetRequiredService<IFileHub>();

        Assert.Same(instance, a);
        Assert.Same(instance, b);
        Assert.Same(instance, asGeneric);
    }

    [Fact]
    public void AddFileHub_DefaultLifetime_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(sp => new MemoryFileHub());

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IMemoryFileHub>();
        var b = provider.GetRequiredService<IMemoryFileHub>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddFileHub_ScopedLifetime_YieldsPerScopeInstance()
    {
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(sp => new MemoryFileHub(), ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        IMemoryFileHub a, b;
        using (var scope = provider.CreateScope())
            a = scope.ServiceProvider.GetRequiredService<IMemoryFileHub>();
        using (var scope = provider.CreateScope())
            b = scope.ServiceProvider.GetRequiredService<IMemoryFileHub>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddFileHub_TransientLifetime_YieldsNewInstancePerResolve()
    {
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(sp => new MemoryFileHub(), ServiceLifetime.Transient);

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IMemoryFileHub>();
        var b = provider.GetRequiredService<IMemoryFileHub>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddFileHub_NoType_FactoryOverload_RegistersOnlyIFileHub()
    {
        var services = new ServiceCollection();
        services.AddFileHub(sp => (IFileHub)new MemoryFileHub());

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IFileHub>());
        Assert.Null(provider.GetService<IMemoryFileHub>());
    }

    [Fact]
    public void AddFileHub_NoType_InstanceOverload_RegistersOnlyIFileHub()
    {
        IFileHub instance = new MemoryFileHub();
        var services = new ServiceCollection();
        services.AddFileHub(instance);

        using var provider = services.BuildServiceProvider();
        Assert.Same(instance, provider.GetRequiredService<IFileHub>());
        Assert.Null(provider.GetService<IMemoryFileHub>());
    }

    [Fact]
    public void AddFileHub_FactoryDisposable_IsDisposedByContainer()
    {
        var services = new ServiceCollection();
        services.AddFileHub<DisposableFileHub>(_ => new DisposableFileHub());

        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<DisposableFileHub>();
        provider.Dispose();

        Assert.True(hub.Disposed);
    }

    [Fact]
    public void AddFileHub_FactoryNull_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => services.AddFileHub<IMemoryFileHub>((Func<IServiceProvider, IMemoryFileHub>)null!));
    }

    [Fact]
    public void AddFileHub_InstanceNull_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => services.AddFileHub<IMemoryFileHub>((IMemoryFileHub)null!));
    }

    private sealed class DisposableFileHub : IFileHub, IDisposable
    {
        public FileDirectory Root { get; } = new MemoryFileHub().Root;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
