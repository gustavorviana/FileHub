using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection
{
    /// <summary>
    /// DI-aware builder for named <see cref="IFileHub"/> entries. Supports
    /// registering a prebuilt instance, or a factory with a
    /// <see cref="ServiceLifetime"/> — use the factory overload when a hub
    /// depends on other DI services (e.g. a per-request <c>ITenantContext</c>).
    /// </summary>
    /// <remarks>
    /// Later registrations under the same name overwrite earlier ones
    /// (case-insensitive). Call sites receive this builder from
    /// <see cref="FileHubServiceCollectionExtensions.AddNamedFileHubs(IServiceCollection, Action{NamedFileHubsServiceBuilder})"/>
    /// and its <see cref="IServiceProvider"/>-aware overload.
    /// </remarks>
    public sealed class NamedFileHubsServiceBuilder
    {
        private readonly Dictionary<string, NamedFileHubEntry> _entries =
            new Dictionary<string, NamedFileHubEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Register a prebuilt <paramref name="hub"/> under <paramref name="name"/>.
        /// The instance is returned on every lookup — equivalent to a singleton
        /// that was provided up-front.
        /// </summary>
        public NamedFileHubsServiceBuilder Register(string name, IFileHub hub)
        {
            ValidateName(name);
            if (hub is null) throw new ArgumentNullException(nameof(hub));
            _entries[name] = NamedFileHubEntry.ForInstance(hub);
            return this;
        }

        /// <summary>
        /// Register a hub under <paramref name="name"/> with a <paramref name="factory"/>
        /// invoked each time a new instance is needed, and a
        /// <paramref name="lifetime"/> that governs caching:
        /// <list type="bullet">
        /// <item><description><see cref="ServiceLifetime.Singleton"/> (default) — factory runs once; the instance is shared across scopes.</description></item>
        /// <item><description><see cref="ServiceLifetime.Scoped"/> — factory runs once per <see cref="IServiceScope"/>; reuse within the scope.</description></item>
        /// <item><description><see cref="ServiceLifetime.Transient"/> — factory runs on every lookup; callers own disposal.</description></item>
        /// </list>
        /// The factory's <see cref="IServiceProvider"/> is the current scope's
        /// provider (root provider for singletons), so tenant-aware code can pull
        /// a scoped <c>ITenantContext</c> or similar at resolution time.
        /// </summary>
        public NamedFileHubsServiceBuilder Register(
            string name,
            Func<IServiceProvider, IFileHub> factory,
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
        {
            ValidateName(name);
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            _entries[name] = NamedFileHubEntry.ForFactory(factory, lifetime);
            return this;
        }

        internal IReadOnlyDictionary<string, NamedFileHubEntry> BuildSnapshot() =>
            new Dictionary<string, NamedFileHubEntry>(_entries, StringComparer.OrdinalIgnoreCase);

        private static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));
        }
    }
}
