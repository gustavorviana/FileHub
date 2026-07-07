using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Local
{
    public class LocalDirectory : FileDirectory
    {
        private DirectoryInfo _info;

        public override string Path { get; }
        public override FileDirectory Parent { get; }

        public override DateTime CreationTimeUtc => RefreshInfo().CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => RefreshInfo().LastWriteTimeUtc;

        internal LocalDirectory(string path, string rootPath, FileDirectory parent)
            : base(GetDirectoryName(path), rootPath)
        {
            Path = path;
            Parent = parent;

            if (!Directory.Exists(Path))
                Directory.CreateDirectory(Path);
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
            PathUtil.ValidateLocalName(head);
            var filePath = ResolveSafePath(head);
            File.Create(filePath).Dispose();
            InvalidateInfo();
            return new LocalFile(this, head);
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
            PathUtil.ValidateLocalName(head);
            file = null;

            var filePath = ResolveSafePath(head);
            if (!File.Exists(filePath))
                return false;

            file = new LocalFile(this, head);
            return true;
        }

        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null)
        {
            ValidatePaging(limit);
            return GetFilesIterator(searchPattern, offset, limit);
        }

        private IEnumerable<FileEntry> GetFilesIterator(string searchPattern, FileListOffset offset, int? limit)
        {
            var dir = new DirectoryInfo(Path);
            IEnumerable<FileInfo> files = dir.GetFiles(searchPattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Where(f => !ShouldSkipLink(f));

            if (offset.IsNamed)
            {
                files = files.Where(f => string.CompareOrdinal(f.Name, offset.Name) >= 0);
            }

            int skipped = 0;
            int yielded = 0;
            foreach (var f in files)
            {
                if (!offset.IsNamed && skipped < offset.Index) { skipped++; continue; }
                if (limit.HasValue && yielded >= limit.Value) yield break;
                yielded++;
                yield return new LocalFile(this, f.Name);
            }
        }

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name)
        {
            ThrowIfReadOnly();
            var segments = PathUtil.SplitAndValidateSegments(name, PathUtil.ValidateLocalName);
            var dirPath = ResolveSafePath(string.Join("/", segments));
            try
            {
                Directory.CreateDirectory(dirPath);
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions to callers.
                if (File.Exists(dirPath))
                    throw new FileAlreadyExistsException(dirPath);
                throw new FileHubException($"Failed to create directory \"{dirPath}\".", ex);
            }
            InvalidateInfo();
            return BuildDirectoryChain(segments);
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            string[] segments;
            try
            {
                segments = PathUtil.SplitAndValidateSegments(name, PathUtil.ValidateLocalName);
            }
            catch (ArgumentException)
            {
                directory = null;
                return false;
            }
            var dirPath = ResolveSafePath(string.Join("/", segments));
            if (!Directory.Exists(dirPath))
            {
                directory = null;
                return false;
            }
            directory = BuildDirectoryChain(segments);
            return true;
        }

        // Local filesystem ops are synchronous; the async surface wraps them.
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

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var dir = new DirectoryInfo(Path);
            foreach (var d in dir.GetDirectories(searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (ShouldSkipLink(d)) continue;
                yield return new LocalDirectory(d.FullName, RootPath, this);
            }
        }

        private LocalDirectory BuildDirectoryChain(string[] segments)
        {
            LocalDirectory current = this;
            foreach (var seg in segments)
            {
                var childPath = System.IO.Path.Combine(current.Path, seg);
                current = new LocalDirectory(childPath, RootPath, current);
            }
            return current;
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
            PathUtil.ValidateLocalName(head);
            return File.Exists(ResolveSafePath(head));
        }

        public override bool DirectoryExists(string name)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (!TryOpenDirectory(head, out var dir)) return false;
                return dir.DirectoryExists(rest);
            }
            PathUtil.ValidateLocalName(head);
            return Directory.Exists(ResolveSafePath(head));
        }

        public override bool Exists() => Directory.Exists(Path);

        public override void Delete()
        {
            ThrowIfReadOnly();
            Directory.Delete(Path, recursive: true);
            InvalidateInfo();
        }

        public override void Delete(string name)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (!TryOpenDirectory(head, out var dir))
                    throw new FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
                dir.Delete(rest);
                return;
            }
            PathUtil.ValidateLocalName(head);
            var fullPath = ResolveSafePath(head);
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
            else if (File.Exists(fullPath))
                File.Delete(fullPath);
            else
                throw new FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
            InvalidateInfo();
        }

        public override FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory under the parent and move
            // into it.
            if (NestedPath.HasSeparator(newName) && Parent != null)
            {
                if (NestedPath.TrySplitLeaf(newName, out var subPath, out var leaf))
                    return MoveTo(Parent.CreateDirectory(subPath), leaf);
                newName = leaf;
            }

            PathUtil.ValidateLocalName(newName);

            var parentPath = System.IO.Path.GetDirectoryName(Path);
            var newPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentPath, newName));

            EnsureWithinRoot(newPath);

            // Rename never overwrites — a name already taken is an error.
            if (Directory.Exists(newPath) || File.Exists(newPath))
                throw new FileAlreadyExistsException(newPath);

            try
            {
                Directory.Move(Path, newPath);
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions to callers.
                if (Directory.Exists(newPath) || File.Exists(newPath))
                    throw new FileAlreadyExistsException(newPath);
                throw new FileHubException($"Failed to rename \"{Path}\" to \"{newName}\".", ex);
            }

            return new LocalDirectory(newPath, RootPath, Parent);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();

            var copied = CopyTo(directory, name);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Rollback: delete the copy that was made to keep state consistent
                try { copied.Delete(); } catch { }
                throw;
            }
            return copied;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name)
        {
            var newDir = directory.CreateDirectory(name);
            CopyContents(this, newDir);
            return newDir;
        }

        // === Helpers ===

        /// <summary>
        /// Resolves a sandbox-safe absolute path for a child entry and verifies
        /// it stays inside the hub root. Exposed so sibling types in the Local
        /// driver (e.g. <see cref="LocalFile"/>) can reuse the sandbox check
        /// without duplicating the root-containment logic.
        /// </summary>
        internal string ResolveSafeChildPath(string childName) => ResolveSafePath(childName);

        /// <summary>
        /// Local paths use the OS-native separator, so combine with
        /// <see cref="System.IO.Path"/> rather than the base <c>/</c> join.
        /// (<c>Path.Combine</c> — <c>Path.Join</c> isn't available on
        /// netstandard2.0 — is equivalent for a leaf name.)
        /// </summary>
        protected internal override string CombineChildPath(string name)
            => System.IO.Path.Combine(Path, name);

        private DirectoryInfo RefreshInfo()
        {
            if (_info == null)
                _info = new DirectoryInfo(Path);
            _info.Refresh();
            return _info;
        }

        private void InvalidateInfo()
        {
            _info = null;
        }

        private static bool ShouldSkipLink(FileSystemInfo info)
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static void CopyContents(FileDirectory source, FileDirectory destination)
        {
            foreach (var file in source.GetFiles())
                file.CopyTo(destination, file.Name);

            foreach (var subDir in source.GetDirectories())
            {
                var newSubDir = destination.CreateDirectory(subDir.Name);
                CopyContents(subDir, newSubDir);
            }
        }

        private static string GetDirectoryName(string path)
        {
            path = path.TrimEnd('/', '\\');
            int index = path.LastIndexOfAny(new[] { '/', '\\' });
            return index == -1 ? path : path.Substring(index + 1);
        }
    }
}
