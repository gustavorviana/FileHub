using FileHub.DependencyInjection;
using FileHub.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection.Tests;

public class KeyedFileHubTests
{
    [Fact]
    public void AddKeyedFileHub_Factory_ResolvesByKey()
    {
        var services = new ServiceCollection();
        services.AddKeyedFileHub<IMemoryFileHub>("uploads", (sp, key) => new MemoryFileHub("uploads-root"));
        services.AddKeyedFileHub<IMemoryFileHub>("logs",    (sp, key) => new MemoryFileHub("logs-root"));

        using var provider = services.BuildServiceProvider();
        var uploads = provider.GetRequiredKeyedService<IMemoryFileHub>("uploads");
        var logs    = provider.GetRequiredKeyedService<IMemoryFileHub>("logs");

        Assert.NotSame(uploads, logs);
        Assert.Equal("uploads-root", uploads.Root.Name);
        Assert.Equal("logs-root", logs.Root.Name);
    }

    [Fact]
    public void AddKeyedFileHub_AlsoRegistersUnderKeyedIFileHub()
    {
        var services = new ServiceCollection();
        services.AddKeyedFileHub<IMemoryFileHub>("uploads", (sp, key) => new MemoryFileHub());

        using var provider = services.BuildServiceProvider();
        var typed = provider.GetRequiredKeyedService<IMemoryFileHub>("uploads");
        var generic = provider.GetRequiredKeyedService<IFileHub>("uploads");

        Assert.Same(typed, generic);
    }

    [Fact]
    public void AddKeyedFileHub_Instance_ResolvedBySameReference()
    {
        var instance = new MemoryFileHub("archive-root");
        var services = new ServiceCollection();
        services.AddKeyedFileHub<IMemoryFileHub>("archive", instance);

        using var provider = services.BuildServiceProvider();
        Assert.Same(instance, provider.GetRequiredKeyedService<IMemoryFileHub>("archive"));
        Assert.Same(instance, provider.GetRequiredKeyedService<IFileHub>("archive"));
    }

    [Fact]
    public void AddKeyedFileHub_WrongKey_Throws()
    {
        var services = new ServiceCollection();
        services.AddKeyedFileHub<IMemoryFileHub>("uploads", (sp, key) => new MemoryFileHub());

        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredKeyedService<IMemoryFileHub>("nope"));
    }

    [Fact]
    public void AddKeyedFileHub_CoexistsWithNonKeyed()
    {
        var services = new ServiceCollection();
        services.AddFileHub<IMemoryFileHub>(sp => new MemoryFileHub("default-root"));
        services.AddKeyedFileHub<IMemoryFileHub>("scratch", (sp, key) => new MemoryFileHub("scratch-root"));

        using var provider = services.BuildServiceProvider();
        var def     = provider.GetRequiredService<IMemoryFileHub>();
        var scratch = provider.GetRequiredKeyedService<IMemoryFileHub>("scratch");

        Assert.NotSame(def, scratch);
        Assert.Equal("default-root", def.Root.Name);
        Assert.Equal("scratch-root", scratch.Root.Name);
    }

    [Fact]
    public void AddKeyedFileHub_DefaultLifetime_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddKeyedFileHub<IMemoryFileHub>("uploads", (sp, key) => new MemoryFileHub());

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredKeyedService<IMemoryFileHub>("uploads");
        var b = provider.GetRequiredKeyedService<IMemoryFileHub>("uploads");

        Assert.Same(a, b);
    }

    [Fact]
    public void AddKeyedFileHub_KeyNull_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => services.AddKeyedFileHub<IMemoryFileHub>(null!, (sp, k) => new MemoryFileHub()));
    }

    [Fact]
    public void AddKeyedFileHub_FactoryNull_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(
            () => services.AddKeyedFileHub<IMemoryFileHub>("uploads", (Func<IServiceProvider, object, IMemoryFileHub>)null!));
    }
}
