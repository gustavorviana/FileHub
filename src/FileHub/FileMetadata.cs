using System;
using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Immutable read-only snapshot of a file's per-object metadata (free-form
    /// user tags plus driver-specific typed fields). Each read returns a fresh
    /// snapshot — drivers replace it wholesale on refresh rather than mutating
    /// in place, so a snapshot a caller is holding never changes underneath it.
    /// To change metadata on a file, pass <see cref="FileWriteOptions"/> to the
    /// next write call.
    /// <para>
    /// Custom drivers build snapshots through the constructor (the tag
    /// dictionary is copied defensively). Driver-specific fields live on a
    /// derived class with its own constructor.
    /// </para>
    /// </summary>
    public class FileMetadata
    {
        private readonly Dictionary<string, string> _tags;

        public FileMetadata(
            string contentType = null,
            string cacheControl = null,
            IReadOnlyDictionary<string, string> tags = null)
        {
            ContentType = contentType;
            CacheControl = cacheControl;
            _tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tags != null)
                foreach (var kv in tags)
                    _tags[kv.Key] = kv.Value;
        }

        /// <summary>Free-form key/value metadata. Public read-only view.</summary>
        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>MIME content type; <c>null</c> when not tracked or not set.</summary>
        public string ContentType { get; }

        /// <summary>HTTP <c>Cache-Control</c> header; <c>null</c> when not tracked or not set.</summary>
        public string CacheControl { get; }
    }
}
