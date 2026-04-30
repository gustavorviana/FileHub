using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileHub.Local
{
    public class LocalDirectory : FileDirectory
    {
        private readonly DirectoryPathMode _pathMode;
        private DirectoryInfo _info;

        public override string Path { get; }
        public override FileDirectory Parent { get; }

        public override DateTime CreationTimeUtc => RefreshInfo().CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => RefreshInfo().LastWriteTimeUtc;

        internal LocalDirectory(string path, string rootPath, FileDirectory parent)
            : this(path, rootPath, parent, DirectoryPathMode.OpenIntermediates) { }

        internal LocalDirectory(string path, string rootPath, FileDirectory parent, DirectoryPathMode pathMode)
            : base(GetDirectoryName(path), rootPath)
        {
            Path = path;
            Parent = parent;
            _pathMode = pathMode;

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
            ValidateName(head);
            var filePath = ResolveSafePath(head);
            File.Create(filePath).Dispose();
            InvalidateInfo();
            return new LocalFile(this, head, RootPath);
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
            ValidateName(head);
            file = null;

            var filePath = ResolveSafePath(head);
            if (!File.Exists(filePath))
                return false;

            file = new LocalFile(this, head, RootPath);
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
                yield return new LocalFile(this, f.Name, RootPath);
            }
        }

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name)
        {
            ThrowIfReadOnly();
            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (_pathMode == DirectoryPathMode.Direct)
                    return CreateDirectoryDirect(name);

                var intermediate = TryOpenDirectory(head, out var existing)
                    ? existing
                    : CreateDirectory(head);
                return intermediate.CreateDirectory(rest);
            }
            var leaf = head ?? name;
            ValidateName(leaf);
            var dirPath = ResolveSafePath(leaf);
            InvalidateInfo();
            return new LocalDirectory(dirPath, RootPath, this, _pathMode);
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (_pathMode == DirectoryPathMode.Direct)
                    return TryOpenDirectoryDirect(name, out directory);

                if (!TryOpenDirectory(head, out var child) || child == null)
                {
                    directory = null;
                    return false;
                }
                return child.TryOpenDirectory(rest, out directory);
            }
            var leaf = head ?? name;
            ValidateName(leaf);
            directory = null;

            var dirPath = ResolveSafePath(leaf);
            if (!Directory.Exists(dirPath))
                return false;

            directory = new LocalDirectory(dirPath, RootPath, this, _pathMode);
            return true;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var dir = new DirectoryInfo(Path);
            foreach (var d in dir.GetDirectories(searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (ShouldSkipLink(d)) continue;
                yield return new LocalDirectory(d.FullName, RootPath, this, _pathMode);
            }
        }

        // --- Direct-mode implementations: resolve nested paths in a single syscall ---

        private LocalDirectory CreateDirectoryDirect(string nestedName)
        {
            var segments = ValidateAndSplitNestedSegments(nestedName);
            var relative = string.Join("/", segments);
            var dirPath = ResolveSafePath(relative);
            Directory.CreateDirectory(dirPath);
            InvalidateInfo();
            return BuildDirectoryChain(segments);
        }

        private bool TryOpenDirectoryDirect(string nestedName, out FileDirectory directory)
        {
            string[] segments;
            try
            {
                segments = ValidateAndSplitNestedSegments(nestedName);
            }
            catch (ArgumentException)
            {
                directory = null;
                return false;
            }
            var relative = string.Join("/", segments);
            var dirPath = ResolveSafePath(relative);
            if (!Directory.Exists(dirPath))
            {
                directory = null;
                return false;
            }
            directory = BuildDirectoryChain(segments);
            return true;
        }

        private static string[] ValidateAndSplitNestedSegments(string nestedName)
        {
            var normalized = nestedName.Replace('\\', '/').Trim('/');
            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                // Keep the exception type aligned with NestedPath.TrySplit — callers
                // expect FileHubException for any path-level traversal attempt.
                if (seg == "." || seg == "..")
                    throw new FileHubException($"Path \"{nestedName}\" contains invalid segment \"{seg}\".");
                ValidateName(seg);
            }
            return segments;
        }

        private LocalDirectory BuildDirectoryChain(string[] segments)
        {
            LocalDirectory current = this;
            foreach (var seg in segments)
            {
                var childPath = System.IO.Path.Combine(current.Path, seg);
                current = new LocalDirectory(childPath, RootPath, current, _pathMode);
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
            ValidateName(head);
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
            ValidateName(head);
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
            ValidateName(head);
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
            ValidateName(newName);

            var parentPath = System.IO.Path.GetDirectoryName(Path);
            var newPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentPath, newName));

            EnsureWithinRoot(newPath);

            Directory.Move(Path, newPath);
            return new LocalDirectory(newPath, RootPath, Parent, _pathMode);
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
        /// Exposes the protected <see cref="FileDirectory.RootPath"/> to sibling
        /// types in the Local driver (e.g. the public <see cref="LocalFile"/>
        /// constructor) so they can stay anchored to the hub's sandbox root.
        /// </summary>
        internal string RootPathInternal => RootPath;

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
