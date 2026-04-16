using System;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection
{
    /// <summary>
    /// Driver-agnostic registration helpers for <see cref="IFileHub"/>.
    /// Consumers supply the concrete factory (or a prebuilt instance) for
    /// the driver of their choice — this library never references specific
    /// driver assemblies.
    /// </summary>
    public static class FileHubServiceCollectionExtensions
    {
        /// <summary>
        /// Register a FileHub under both <typeparamref name="TFileHub"/> and
        /// <see cref="IFileHub"/>. Consumers can inject either.
        /// </summary>
        public static IServiceCollection AddFileHub<TFileHub>(
            this IServiceCollection services,
            Func<IServiceProvider, TFileHub> factory,
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TFileHub : class, IFileHub
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            services.Add(new ServiceDescriptor(typeof(TFileHub), factory, lifetime));
            services.Add(new ServiceDescriptor(
                typeof(IFileHub),
                sp => sp.GetRequiredService<TFileHub>(),
                lifetime));
            return services;
        }

        /// <summary>
        /// Register a prebuilt FileHub instance (Singleton) under both
        /// <typeparamref name="TFileHub"/> and <see cref="IFileHub"/>.
        /// </summary>
        public static IServiceCollection AddFileHub<TFileHub>(
            this IServiceCollection services,
            TFileHub instance)
            where TFileHub : class, IFileHub
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            services.Add(new ServiceDescriptor(typeof(TFileHub), instance));
            services.Add(new ServiceDescriptor(
                typeof(IFileHub),
                sp => sp.GetRequiredService<TFileHub>(),
                ServiceLifetime.Singleton));
            return services;
        }

        /// <summary>
        /// Register a FileHub under <see cref="IFileHub"/> only — no driver-typed
        /// entry. Useful when the consumer only injects <c>IFileHub</c>.
        /// </summary>
        public static IServiceCollection AddFileHub(
            this IServiceCollection services,
            Func<IServiceProvider, IFileHub> factory,
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            services.Add(new ServiceDescriptor(typeof(IFileHub), factory, lifetime));
            return services;
        }

        /// <summary>
        /// Register a prebuilt <see cref="IFileHub"/> instance as Singleton.
        /// </summary>
        public static IServiceCollection AddFileHub(
            this IServiceCollection services,
            IFileHub instance)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            services.Add(new ServiceDescriptor(typeof(IFileHub), instance));
            return services;
        }
        /// <summary>
        /// Register a singleton <see cref="INamedFileHubs"/> registry. The
        /// <paramref name="configure"/> callback receives a
        /// <see cref="NamedFileHubsBuilder"/>; call <see cref="NamedFileHubsBuilder.Register"/>
        /// on it to add hubs by name. The resulting registry is immutable — once
        /// <c>AddNamedFileHubs</c> returns, no further hubs can be registered.
        /// Hubs are <b>not</b> exposed as keyed DI services.
        /// </summary>
        /// <example>
        /// <code>
        /// services.AddNamedFileHubs(builder =>
        /// {
        ///     builder.Register("reports", new MemoryFileHub());
        ///     builder.Register("logs",    new LocalFileHub(@"C:\logs"));
        /// });
        /// </code>
        /// </example>
        public static IServiceCollection AddNamedFileHubs(
            this IServiceCollection services,
            Action<NamedFileHubsBuilder> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var builder = new NamedFileHubsBuilder();
            configure(builder);
            services.Add(new ServiceDescriptor(typeof(INamedFileHubs), builder.Build()));
            return services;
        }

        /// <summary>
        /// Register a singleton <see cref="INamedFileHubs"/> registry. The
        /// <paramref name="configure"/> callback receives the resolved
        /// <see cref="IServiceProvider"/> and a <see cref="NamedFileHubsBuilder"/>
        /// — useful when hub construction depends on other services
        /// (e.g. <c>IOptions&lt;T&gt;</c>). The resulting registry is immutable
        /// once the registry is built.
        /// </summary>
        public static IServiceCollection AddNamedFileHubs(
            this IServiceCollection services,
            Action<IServiceProvider, NamedFileHubsBuilder> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            services.Add(new ServiceDescriptor(typeof(INamedFileHubs), sp =>
            {
                var builder = new NamedFileHubsBuilder();
                configure(sp, builder);
                return builder.Build();
            }, ServiceLifetime.Singleton));
            return services;
        }
    }
}
