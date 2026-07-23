using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Memory
{
    public class MemoryDirectory : FileDirectory
    {
        private readonly Dictionary<string, MemoryFileData> _files
            = new Dictionary<string, MemoryFileData>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MemoryDirectory> _directories
            = new Dictionary<string, MemoryDirectory>(StringComparer.OrdinalIgnoreCase);

        private readonly MemoryDirectory _parent;

        public override string Path { get; }
        public override FileDirectory Parent => _parent;
        public override DateTime CreationTimeUtc { get; }
        public override DateTime LastWriteTimeUtc { get; }

        public MemoryDirectory(string name, MemoryDirectory parent = null)
            : base(name, rootPath: null)
        {
            _parent = parent;
            // Driver-neutral "/" join (see PathUtil.JoinDisplay) — Memory is a
            // logical key store, so paths stay OS-independent and never double
            // the separator at the root.
            Path = parent != null
                ? PathUtil.JoinDisplay(parent.Path, name)
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
            // A file and a directory cannot share a name (mirrors the
            // local-filesystem driver).
            if (_directories.ContainsKey(head))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(Path, head));
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

        // === Directory resolution (whole path; in-process walk, no I/O) ===

        public override FileDirectory CreateDirectory(string name)
        {
            ThrowIfReadOnly();
            var current = this;
            foreach (var seg in PathUtil.SplitAndValidateSegments(name))
            {
                if (!current._directories.TryGetValue(seg, out var child))
                {
                    // A file and a directory cannot share a name (mirrors the
                    // local-filesystem driver).
                    if (current._files.ContainsKey(seg))
                        throw new FileAlreadyExistsException(PathUtil.JoinDisplay(current.Path, seg));
                    child = new MemoryDirectory(seg, current);
                    current._directories[seg] = child;
                }
                current = child;
            }
            return current;
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            directory = null;
            // Invalid leaf name → not found (false); absolute/traversal are
            // security violations and propagate as FileHubException.
            string[] segments;
            try { segments = PathUtil.SplitAndValidateSegments(name); }
            catch (ArgumentException) { return false; }

            var current = this;
            foreach (var seg in segments)
            {
                if (!current._directories.TryGetValue(seg, out var child))
                    return false;
                current = child;
            }
            directory = current;
            return true;
        }

        // In-memory ops are synchronous; the async surface wraps them.
        public override Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFile(name));
        }

        public override Task<(FileEntry File, bool Exists)> TryOpenFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = TryOpenFile(name, out var file);
            return Task.FromResult((file, exists));
        }

        public override Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDirectory(name));
        }

        public override Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = TryOpenDirectory(name, out var directory);
            return Task.FromResult((directory, exists));
        }

        public override Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(FileExists(name));
        }

        public override Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DirectoryExists(name));
        }

        public override Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete();
            return Task.CompletedTask;
        }

        public override Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(name);
            return Task.CompletedTask;
        }

        public override Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Rename(newName));
        }

        public override Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MoveTo(directory, name));
        }

        public override Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CopyTo(directory, name));
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            return FilterByPattern(_directories.Keys, searchPattern)
                .Select(name => (FileDirectory)_directories[name]);
        }

        // === Common operations ===

        public override bool FileExists(string name)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (!TryOpenDirectory(head, out var dir)) return false;
                return dir.FileExists(rest);
            }
            ValidateName(head);
            return _files.ContainsKey(head);
        }

        public override bool DirectoryExists(string name)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (!TryOpenDirectory(head, out var dir)) return false;
                return dir.DirectoryExists(rest);
            }
            ValidateName(head);
            return _directories.ContainsKey(head);
        }

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
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (!TryOpenDirectory(head, out var dir))
                    throw new System.IO.FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
                dir.Delete(rest);
                return;
            }
            ValidateName(head);
            if (_files.Remove(head)) return;
            if (_directories.Remove(head)) return;
            throw new System.IO.FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
        }

        public override FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and move into it.
            if (NestedPath.HasSeparator(newName) && Parent != null)
            {
                if (NestedPath.TrySplitLeaf(newName, out var subPath, out var leaf))
                    return MoveTo(Parent.CreateDirectory(subPath), leaf);
                newName = leaf;
            }

            ValidateName(newName);

            // Rename never overwrites — a name already taken is an error.
            if (_parent != null && _parent.Exists(newName))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(_parent.Path, newName));

            _parent?.RemoveDirectory(Name);
            var renamed = new MemoryDirectory(newName, _parent);
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
                var subDir = new MemoryDirectory(kvp.Key, destination);
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
            if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*")
                return names;

            var regex = GlobToRegex(pattern);
            return names.Where(n => regex.IsMatch(n));
        }

        private static Regex GlobToRegex(string pattern)
        {
            var sb = new StringBuilder(pattern.Length + 8);
            sb.Append('^');
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                        sb.Append(".*");
                        break;
                    case '?':
                        sb.Append('.');
                        break;
                    case '[':
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0)
                        {
                            sb.Append("\\[");
                        }
                        else
                        {
                            sb.Append('[');
                            for (var j = i + 1; j < close; j++)
                            {
                                var inner = pattern[j];
                                if (inner == '\\' || inner == '^' || inner == ']')
                                    sb.Append('\\');
                                sb.Append(inner);
                            }
                            sb.Append(']');
                            i = close;
                        }
                        break;
                    default:
                        if ("\\.+()|{}^$".IndexOf(c) >= 0)
                            sb.Append('\\');
                        sb.Append(c);
                        break;
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
