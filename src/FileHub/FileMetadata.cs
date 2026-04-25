using System;
using System.Collections;
using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Mutable metadata surface attached to a file (key/value tags plus
    /// driver-specific typed fields). Lives on <see cref="IMetadataAware.Metadata"/>.
    /// Set values freely — the driver applies them on the next alteration
    /// operation (write, copy, move) when <see cref="IsModified"/> is true,
    /// then marks the snapshot synced.
    /// </summary>
    public class FileMetadata
    {
        private readonly TagDictionary _tags;

        /// <summary>
        /// Free-form key/value metadata. On S3 maps to user-metadata
        /// (<c>x-amz-meta-*</c>); on OCI to <c>opc-meta-*</c>. Mutations
        /// (set, remove, clear) flip <see cref="IsModified"/> to <c>true</c>.
        /// </summary>
        public IDictionary<string, string> Tags => _tags;

        /// <summary>
        /// <c>true</c> when any field (base <see cref="Tags"/> or a
        /// driver-specific typed property) has been mutated since the last
        /// sync with the store. Drivers clear this after a successful apply.
        /// </summary>
        public bool IsModified { get; private set; }

        public FileMetadata()
        {
            _tags = new TagDictionary(this);
        }

        /// <summary>
        /// Driver-only: initialize the snapshot from server-side values
        /// without flipping <see cref="IsModified"/>.
        /// </summary>
        public void LoadSynced(IReadOnlyDictionary<string, string> tags)
        {
            _tags.ResetTo(tags);
            IsModified = false;
        }

        /// <summary>Driver-only: mark the snapshot as synced after a successful apply.</summary>
        public void MarkSynced() => IsModified = false;

        /// <summary>Mark the snapshot as dirty — called by derived classes when typed properties change.</summary>
        protected void MarkModified() => IsModified = true;

        private sealed class TagDictionary : IDictionary<string, string>, IReadOnlyDictionary<string, string>
        {
            private readonly Dictionary<string, string> _inner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly FileMetadata _owner;

            public TagDictionary(FileMetadata owner) { _owner = owner; }

            public string this[string key]
            {
                get => _inner[key];
                set
                {
                    if (_inner.TryGetValue(key, out var existing) && existing == value) return;
                    _inner[key] = value;
                    _owner.MarkModified();
                }
            }

            public ICollection<string> Keys => _inner.Keys;
            public ICollection<string> Values => _inner.Values;
            IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => _inner.Keys;
            IEnumerable<string> IReadOnlyDictionary<string, string>.Values => _inner.Values;
            public int Count => _inner.Count;
            public bool IsReadOnly => false;

            public void Add(string key, string value)
            {
                _inner.Add(key, value);
                _owner.MarkModified();
            }
            public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

            public void Clear()
            {
                if (_inner.Count == 0) return;
                _inner.Clear();
                _owner.MarkModified();
            }

            public bool Contains(KeyValuePair<string, string> item)
                => ((ICollection<KeyValuePair<string, string>>)_inner).Contains(item);

            public bool ContainsKey(string key) => _inner.ContainsKey(key);

            public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
                => ((ICollection<KeyValuePair<string, string>>)_inner).CopyTo(array, arrayIndex);

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

            public bool Remove(string key)
            {
                if (_inner.Remove(key))
                {
                    _owner.MarkModified();
                    return true;
                }
                return false;
            }
            public bool Remove(KeyValuePair<string, string> item)
            {
                if (((ICollection<KeyValuePair<string, string>>)_inner).Remove(item))
                {
                    _owner.MarkModified();
                    return true;
                }
                return false;
            }

            public bool TryGetValue(string key, out string value) => _inner.TryGetValue(key, out value);

            internal void ResetTo(IReadOnlyDictionary<string, string> tags)
            {
                _inner.Clear();
                if (tags == null) return;
                foreach (var kv in tags)
                    _inner[kv.Key] = kv.Value;
            }
        }
    }
}
