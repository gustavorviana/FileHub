using System;
using System.Collections.Generic;

namespace FileHub.DependencyInjection
{
    /// <summary>
    /// Holds singleton-lifetime named hubs that were created from a factory.
    /// Registered as a singleton itself so the root provider is the one passed
    /// to factories — this prevents scoped dependencies from leaking into
    /// singleton hubs (a captive-dependency bug).
    /// </summary>
    internal sealed class NamedFileHubsSingletonCache : IDisposable
    {
        private readonly IServiceProvider _rootProvider;
        private readonly Dictionary<string, IFileHub> _cache =
            new Dictionary<string, IFileHub>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();
        private bool _disposed;

        public NamedFileHubsSingletonCache(IServiceProvider rootProvider)
        {
            _rootProvider = rootProvider;
        }

        public IFileHub GetOrCreate(string name, NamedFileHubEntry entry)
        {
            if (entry.Instance != null) return entry.Instance;

            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(NamedFileHubsSingletonCache));
                if (_cache.TryGetValue(name, out var existing)) return existing;
                var hub = entry.Factory(_rootProvider);
                _cache[name] = hub;
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
                toDispose = new IFileHub[_cache.Count];
                _cache.Values.CopyTo(toDispose, 0);
                _cache.Clear();
            }
            foreach (var hub in toDispose)
                (hub as IDisposable)?.Dispose();
        }
    }
}
