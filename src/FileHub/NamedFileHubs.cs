using System;
using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Default <see cref="INamedFileHubs"/> implementation.
    /// Immutable after construction — built through <see cref="NamedFileHubsBuilder"/>.
    /// Name matching is ordinal, case-insensitive.
    /// </summary>
    /// <example>
    /// Standalone:
    /// <code>
    /// INamedFileHubs hubs = new NamedFileHubsBuilder()
    ///     .Register("reports", new MemoryFileHub())
    ///     .Register("logs",    new LocalFileHub(@"C:\logs"))
    ///     .Build();
    ///
    /// var root = hubs.GetRootByName("reports");
    /// </code>
    /// </example>
    public sealed class NamedFileHubs : INamedFileHubs
    {
        private readonly Dictionary<string, IFileHub> _hubs;

        internal NamedFileHubs(IDictionary<string, IFileHub> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            _hubs = new Dictionary<string, IFileHub>(entries, StringComparer.OrdinalIgnoreCase);
        }

        public IFileHub GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _hubs.TryGetValue(name, out var hub) ? hub : null;
        }

        public FileDirectory GetRootByName(string name) => GetByName(name)?.Root;
    }
}
