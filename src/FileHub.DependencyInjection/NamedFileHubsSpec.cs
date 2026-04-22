using System.Collections.Generic;

namespace FileHub.DependencyInjection
{
    /// <summary>
    /// Immutable snapshot of named-hub registrations. Registered as a singleton
    /// so the entry table is shared across scopes.
    /// </summary>
    internal sealed class NamedFileHubsSpec
    {
        public NamedFileHubsSpec(IReadOnlyDictionary<string, NamedFileHubEntry> entries)
        {
            Entries = entries;
        }

        public IReadOnlyDictionary<string, NamedFileHubEntry> Entries { get; }
    }
}
