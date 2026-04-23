using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace FileHub.DependencyInjection
{
    /// <summary>
    /// Scoped <see cref="INamedFileHubs"/> implementation. Respects the
    /// per-entry <see cref="ServiceLifetime"/> recorded on the
    /// <see cref="NamedFileHubsSpec"/>: singletons flow through the shared
    /// cache, scoped entries are cached per <see cref="IServiceScope"/> on
    /// this instance, and transients run the factory on every lookup.
    /// </summary>
    internal sealed class DiNamedFileHubs : INamedFileHubs, IDisposable
    {
        private readonly IReadOnlyDictionary<string, NamedFileHubEntry> _entries;
        private readonly NamedFileHubsSingletonCache _singletons;
        private readonly IServiceProvider _scopeProvider;
        private readonly Dictionary<string, IFileHub> _scopedCache =
            new Dictionary<string, IFileHub>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();
        private bool _disposed;

        public DiNamedFileHubs(
            NamedFileHubsSpec spec,
            NamedFileHubsSingletonCache singletons,
            IServiceProvider scopeProvider)
        {
            _entries = spec.Entries;
            _singletons = singletons;
            _scopeProvider = scopeProvider;
        }

        public IFileHub GetByName(string name)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DiNamedFileHubs));
            if (string.IsNullOrEmpty(name)) return null;
            if (!_entries.TryGetValue(name, out var entry)) return null;

            switch (entry.Lifetime)
            {
                case ServiceLifetime.Singleton:
                    return _singletons.GetOrCreate(name, entry);
                case ServiceLifetime.Scoped:
                    return ResolveScoped(name, entry);
                default: // Transient
                    return entry.Factory(_scopeProvider);
            }
        }

        public FileDirectory GetRootByName(string name) => GetByName(name)?.Root;

        private IFileHub ResolveScoped(string name, NamedFileHubEntry entry)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DiNamedFileHubs));
                if (_scopedCache.TryGetValue(name, out var existing)) return existing;
                var hub = entry.Factory(_scopeProvider);
                _scopedCache[name] = hub;
                return hub;
            }
        }

        public void Dispose()
        {
            IFileHub[] toDispose;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                toDispose = new IFileHub[_scopedCache.Count];
                _scopedCache.Values.CopyTo(toDispose, 0);
                _scopedCache.Clear();
            }
            foreach (var hub in toDispose)
                (hub as IDisposable)?.Dispose();
        }
    }
}
