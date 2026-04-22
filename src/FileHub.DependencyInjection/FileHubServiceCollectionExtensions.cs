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
        /// Register an <see cref="INamedFileHubs"/> registry. The
        /// <paramref name="configure"/> callback receives a
        /// <see cref="NamedFileHubsServiceBuilder"/>; call
        /// <see cref="NamedFileHubsServiceBuilder.Register(string, IFileHub)"/> for
        /// prebuilt singletons, or
        /// <see cref="NamedFileHubsServiceBuilder.Register(string, Func{IServiceProvider, IFileHub}, ServiceLifetime)"/>
        /// when hub construction depends on DI services (tenant context, options,
        /// etc.) and you need per-entry lifetime control (singleton, scoped,
        /// transient).
        /// </summary>
        /// <remarks>
        /// The registry itself is registered as a scoped service so scoped and
        /// transient hubs honour <see cref="IServiceScope"/> boundaries; singleton
        /// hubs are cached once at the root. Hubs are <b>not</b> exposed as keyed
        /// DI services — access is only through <see cref="INamedFileHubs"/>.
        /// </remarks>
        /// <example>
        /// <code>
        /// services.AddNamedFileHubs(builder =>
        /// {
        ///     builder.Register("reports", new MemoryFileHub());
        ///     builder.Register(
        ///         "tenant",
        ///         sp => new LocalFileHub($@"C:\tenants\{sp.GetRequiredService&lt;ITenantContext&gt;().Id}"),
        ///         ServiceLifetime.Scoped);
        /// });
        /// </code>
        /// </example>
        public static IServiceCollection AddNamedFileHubs(
            this IServiceCollection services,
            Action<NamedFileHubsServiceBuilder> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var builder = new NamedFileHubsServiceBuilder();
            configure(builder);
            RegisterNamedFileHubsServices(services, new NamedFileHubsSpec(builder.BuildSnapshot()));
            return services;
        }

        /// <summary>
        /// Register an <see cref="INamedFileHubs"/> registry where the
        /// <paramref name="configure"/> callback gets an
        /// <see cref="IServiceProvider"/> for one-time setup lookups
        /// (e.g. <c>IOptions&lt;T&gt;</c>). The inner factory passed to
        /// <see cref="NamedFileHubsServiceBuilder.Register(string, Func{IServiceProvider, IFileHub}, ServiceLifetime)"/>
        /// still runs per lookup with the current scope's provider.
        /// </summary>
        public static IServiceCollection AddNamedFileHubs(
            this IServiceCollection services,
            Action<IServiceProvider, NamedFileHubsServiceBuilder> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            services.Add(new ServiceDescriptor(typeof(NamedFileHubsSpec), sp =>
            {
                var builder = new NamedFileHubsServiceBuilder();
                configure(sp, builder);
                return new NamedFileHubsSpec(builder.BuildSnapshot());
            }, ServiceLifetime.Singleton));
            services.Add(new ServiceDescriptor(typeof(NamedFileHubsSingletonCache), typeof(NamedFileHubsSingletonCache), ServiceLifetime.Singleton));
            services.Add(new ServiceDescriptor(typeof(INamedFileHubs), typeof(DiNamedFileHubs), ServiceLifetime.Scoped));
            return services;
        }

        private static void RegisterNamedFileHubsServices(IServiceCollection services, NamedFileHubsSpec spec)
        {
            services.Add(new ServiceDescriptor(typeof(NamedFileHubsSpec), spec));
            services.Add(new ServiceDescriptor(typeof(NamedFileHubsSingletonCache), typeof(NamedFileHubsSingletonCache), ServiceLifetime.Singleton));
            services.Add(new ServiceDescriptor(typeof(INamedFileHubs), typeof(DiNamedFileHubs), ServiceLifetime.Scoped));
        }
    }
}
