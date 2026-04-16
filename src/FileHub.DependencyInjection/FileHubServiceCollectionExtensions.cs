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
        // ---- Non-keyed ----

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

        // ---- Keyed (named, .NET 8+) ----

        /// <summary>
        /// Register a keyed FileHub under both the keyed <typeparamref name="TFileHub"/>
        /// slot and the keyed <see cref="IFileHub"/> slot with the same
        /// <paramref name="serviceKey"/>.
        /// </summary>
        public static IServiceCollection AddKeyedFileHub<TFileHub>(
            this IServiceCollection services,
            object serviceKey,
            Func<IServiceProvider, object, TFileHub> factory,
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TFileHub : class, IFileHub
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (serviceKey == null) throw new ArgumentNullException(nameof(serviceKey));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            services.Add(new ServiceDescriptor(typeof(TFileHub), serviceKey, factory, lifetime));
            services.Add(new ServiceDescriptor(
                typeof(IFileHub),
                serviceKey,
                (sp, key) => sp.GetRequiredKeyedService<TFileHub>(key),
                lifetime));
            return services;
        }

        /// <summary>
        /// Register a prebuilt keyed FileHub instance (Singleton) under both
        /// the keyed <typeparamref name="TFileHub"/> slot and the keyed
        /// <see cref="IFileHub"/> slot.
        /// </summary>
        public static IServiceCollection AddKeyedFileHub<TFileHub>(
            this IServiceCollection services,
            object serviceKey,
            TFileHub instance)
            where TFileHub : class, IFileHub
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (serviceKey == null) throw new ArgumentNullException(nameof(serviceKey));
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            services.Add(new ServiceDescriptor(typeof(TFileHub), serviceKey, instance));
            services.Add(new ServiceDescriptor(
                typeof(IFileHub),
                serviceKey,
                (sp, key) => sp.GetRequiredKeyedService<TFileHub>(key),
                ServiceLifetime.Singleton));
            return services;
        }
    }
}
