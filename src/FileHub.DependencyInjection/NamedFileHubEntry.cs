using System;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection
{
    internal sealed class NamedFileHubEntry
    {
        private NamedFileHubEntry(IFileHub instance, Func<IServiceProvider, IFileHub> factory, ServiceLifetime lifetime)
        {
            Instance = instance;
            Factory = factory;
            Lifetime = lifetime;
        }

        public IFileHub Instance { get; }
        public Func<IServiceProvider, IFileHub> Factory { get; }
        public ServiceLifetime Lifetime { get; }

        public static NamedFileHubEntry ForInstance(IFileHub instance) =>
            new NamedFileHubEntry(instance, null, ServiceLifetime.Singleton);

        public static NamedFileHubEntry ForFactory(Func<IServiceProvider, IFileHub> factory, ServiceLifetime lifetime) =>
            new NamedFileHubEntry(null, factory, lifetime);
    }
}
