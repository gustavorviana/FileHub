using System;
using System.Collections.Generic;

namespace FileHub;

/// <summary>
/// Builder for <see cref="NamedFileHubs"/>. Add entries with <see cref="Register"/>,
/// then call <see cref="Build"/> to produce the immutable registry. Later
/// registrations under the same name overwrite earlier ones.
/// </summary>
public sealed class NamedFileHubsBuilder
{
    private readonly Dictionary<string, IFileHub> _hubs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Add <paramref name="hub"/> under <paramref name="name"/>. Returns
    /// <c>this</c> so calls can be chained.
    /// </summary>
    public NamedFileHubsBuilder Register(string name, IFileHub hub)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be null or empty.", nameof(name));
        if (hub is null) throw new ArgumentNullException(nameof(hub));

        _hubs[name] = hub;
        return this;
    }

    /// <summary>
    /// Produce the immutable <see cref="NamedFileHubs"/>. The builder may be
    /// discarded after this call; no further mutation on the returned registry
    /// is possible.
    /// </summary>
    public NamedFileHubs Build() => new(_hubs);
}
