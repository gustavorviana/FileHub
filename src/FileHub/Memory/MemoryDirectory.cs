using System;
using System.Collections.Generic;
using System.Linq;

namespace FileHub.Memory
{
    public class MemoryDirectory : FileDirectory
    {
        private readonly Dictionary<string, MemoryFileData> _files
            = new Dictionary<string, MemoryFileData>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MemoryDirectory> _directories
            = new Dictionary<string, MemoryDirectory>(StringComparer.OrdinalIgnoreCase);

        private readonly MemoryDirectory _parent;
        private readonly DirectoryPathMode _pathMode;

        public override string Path { get; }
        public override FileDirectory Parent => _parent;
        public override DateTime CreationTimeUtc { get; }
        public override DateTime LastWriteTimeUtc { get; }

        public MemoryDirectory(string name, MemoryDirectory parent = null)
            : this(name, parent, DirectoryPathMode.OpenIntermediates) { }

        public MemoryDirectory(string name, MemoryDirectory parent, DirectoryPathMode pathMode)
            : base(name, rootPath: null)
        {
            _parent = parent;
            _pathMode = pathMode;
            Path = parent != null
                ? System.IO.Path.Combine(parent.Path, name)
                : name;
            CreationTimeUtc = DateTime.UtcNow;
            LastWriteTimeUtc = CreationTimeUtc;
        }

        // === File operations ===

        public override FileEntry CreateFile(string name)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = OpenOrCreateChildDirectory(head, createIfNotExists: true);
                return dir.CreateFile(rest);
            }
            ValidateName(head);
            var data = new MemoryFileData(head);
            _files[head] = data;
            return new MemoryFile(this, data);
        }

        public override bool TryOpenFile(string name, out FileEntry file)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (!TryOpenDirectory(head, out var dir))
                {
                    file = null;
                    return false;
                }
                return dir.TryOpenFile(rest, out file);
            }
            file = null;
            if (!_files.TryGetValue(head, out var data))
                return false;

            file = new MemoryFile(this, data);
            return true;
        }

        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null)
        {
            ValidatePaging(limit);
            IEnumerable<string> names = FilterByPattern(_files.Keys, searchPattern).OrderBy(n => n, StringComparer.Ordinal);

            if (offset.IsNamed)
            {
                names = names.Where(n => string.CompareOrdinal(n, offset.Name) >= 0);
            }
            else if (offset.Index > 0)
            {
                names = names.Skip(offset.Index);
            }

            if (limit.HasValue) names = names.Take(limit.Value);
            return names.Select(name => (FileEntry)new MemoryFile(this, _files[name]));
        }

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name)
        {
            ThrowIfReadOnly();
            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                var intermediate = TryOpenDirectory(head, out var existing)
                    ? existing
                    : CreateDirectory(head);
                return intermediate.CreateDirectory(rest);
            }
            ValidateName(name);
            var dir = new MemoryDirectory(name, this, _pathMode);
            _directories[name] = dir;
            return dir;
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (!TryOpenDirectory(head, out var child) || child == null)
                {
                    directory = null;
                    return false;
                }
                return child.TryOpenDirectory(rest, out directory);
            }
            directory = null;
            if (!_directories.TryGetValue(name, out var dir))
                return false;

            directory = dir;
            return true;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            return FilterByPattern(_directories.Keys, searchPattern)
                .Select(name => (FileDirectory)_directories[name]);
        }

        // === Common operations ===

        public override bool FileExists(string name) => _files.ContainsKey(name);
        public override bool DirectoryExists(string name) => _directories.ContainsKey(name);

        public override bool Exists() => !Disposed;

        public override void Delete()
        {
            ThrowIfReadOnly();
            _files.Clear();
            _directories.Clear();
            _parent?.RemoveDirectory(Name);
            Dispose();
        }

        public override void Delete(string name)
        {
            ThrowIfReadOnly();
            if (_files.Remove(name)) return;
            if (_directories.Remove(name)) return;
            throw new System.IO.FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
        }

        public override FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();
            ValidateName(newName);
            _parent?.RemoveDirectory(Name);
            var renamed = new MemoryDirectory(newName, _parent, _pathMode);
            CopyContentsTo(this, renamed);
            _parent?.AddDirectory(renamed);

            // Clear and dispose the old instance so stale references stop reporting as alive.
            _files.Clear();
            _directories.Clear();
            Dispose();
            return renamed;
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();
            var copied = CopyTo(directory, name);
            _parent?.RemoveDirectory(Name);

            // Clear and dispose the old instance so stale references stop reporting as alive.
            _files.Clear();
            _directories.Clear();
            Dispose();
            return copied;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name)
        {
            var newDir = directory.CreateDirectory(name);
            if (newDir is MemoryDirectory memDir)
                CopyContentsTo(this, memDir);
            else
                CopyContentsGeneric(this, newDir);
            return newDir;
        }

        // === Internal helpers ===

        internal bool ContainsFile(string name) => _files.ContainsKey(name);
        internal void RemoveFile(string name) => _files.Remove(name);
        internal void AddFile(MemoryFileData data) => _files[data.Name] = data;
        internal void RemoveDirectory(string name) => _directories.Remove(name);
        internal void AddDirectory(MemoryDirectory dir) => _directories[dir.Name] = dir;

        // === Private helpers ===

        private static void CopyContentsTo(MemoryDirectory source, MemoryDirectory destination)
        {
            foreach (var kvp in source._files)
                destination._files[kvp.Key] = kvp.Value.Clone();

            foreach (var kvp in source._directories)
            {
                var subDir = new MemoryDirectory(kvp.Key, destination, destination._pathMode);
                CopyContentsTo(kvp.Value, subDir);
                destination._directories[kvp.Key] = subDir;
            }
        }

        private static void CopyContentsGeneric(FileDirectory source, FileDirectory destination)
        {
            foreach (var file in source.GetFiles())
                file.CopyTo(destination, file.Name);

            foreach (var subDir in source.GetDirectories())
            {
                var newSubDir = destination.CreateDirectory(subDir.Name);
                CopyContentsGeneric(subDir, newSubDir);
            }
        }

        private static IEnumerable<string> FilterByPattern(IEnumerable<string> names, string pattern)
        {
            if (pattern == "*" || pattern == "*.*")
                return names;

            if (pattern.StartsWith("*") && pattern.LastIndexOf('*') == 0)
            {
                var suffix = pattern.Substring(1);
                return names.Where(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            }

            if (pattern.EndsWith("*") && pattern.IndexOf('*') == pattern.Length - 1)
            {
                var prefix = pattern.Substring(0, pattern.Length - 1);
                return names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }

            return names.Where(n => string.Equals(n, pattern, StringComparison.OrdinalIgnoreCase));
        }
    }
}
