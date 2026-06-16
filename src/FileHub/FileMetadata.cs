using System;
using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Read-only snapshot of a file's per-object metadata (free-form user
    /// tags plus driver-specific typed fields). To change metadata on a file,
    /// pass <see cref="FileWriteOptions"/> to the next write call.
    /// <para>
    /// Custom drivers populate fields via the <c>protected internal</c>
    /// setters and <see cref="SetTags(IReadOnlyDictionary{string,string})"/>
    /// from inside a derived class.
    /// </para>
    /// </summary>
    public class FileMetadata
    {
        private readonly Dictionary<string, string> _tags =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Free-form key/value metadata. Public read-only view.</summary>
        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>MIME content type; <c>null</c> when not tracked or not set.</summary>
        public string ContentType { get; protected internal set; }

        /// <summary>HTTP <c>Cache-Control</c> header; <c>null</c> when not tracked or not set.</summary>
        public string CacheControl { get; protected internal set; }

        /// <summary>
        /// Driver-internal / derived-class: replace the tag dictionary in bulk.
        /// </summary>
        protected internal void SetTags(IReadOnlyDictionary<string, string> tags)
        {
            _tags.Clear();
            if (tags == null) return;
            foreach (var kv in tags)
                _tags[kv.Key] = kv.Value;
        }
    }
}
