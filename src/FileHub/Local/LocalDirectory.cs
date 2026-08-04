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
            if (Directory.Exists(filePath) || File.Exists(filePath))
                throw new FileAlreadyExistsException(filePath);

            try
            {
                File.Create(filePath).Dispose();
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions to callers.
                if (Directory.Exists(filePath))
                    throw new FileAlreadyExistsException(filePath);

                throw new FileHubException($"Failed to create file \"{filePath}\".", ex);
            }
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

        public override Task DeleteAsync(bool recursive = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(recursive);
            return Task.CompletedTask;
        }

        public override Task DeleteAsync(string name, bool recursive = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(name, recursive);
            return Task.CompletedTask;
        }

        public override Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Rename(newName));
        }

        public override Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MoveTo(directory, name, overwrite));
        }

        public override Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CopyTo(directory, name, overwrite));
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

        public override void Delete(bool recursive = false)
        {
            ThrowIfReadOnly();
            // Surface the non-empty case as the library's own type instead of a
            // raw System.IO.IOException wrapped into a generic FileHubException.
            if (!recursive && Directory.Exists(Path) && Directory.EnumerateFileSystemEntries(Path).Any())
                throw new DirectoryNotEmptyException(Path);
            try
            {
                Directory.Delete(Path, recursive);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone — deletion is idempotent.
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions to callers.
                throw new FileHubException($"Failed to delete directory \"{Path}\".", ex);
            }
            InvalidateInfo();
        }

        public override void Delete(string name, bool recursive = false)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                if (TryOpenDirectory(head, out var dir))
                    dir.Delete(rest, recursive);
                return;
            }
            PathUtil.ValidateLocalName(head);
            var fullPath = ResolveSafePath(head);

            var isDir = Directory.Exists(fullPath);
            var isFile = !isDir && File.Exists(fullPath);
            if (!isDir && !isFile)
                return;

            if (isDir && !recursive && Directory.EnumerateFileSystemEntries(fullPath).Any())
                throw new DirectoryNotEmptyException(fullPath);

            try
            {
                if (isDir)
                    Directory.Delete(fullPath, recursive);
                else
                    File.Delete(fullPath);
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions to callers.
                throw new FileHubException($"Failed to delete \"{fullPath}\".", ex);
            }
            InvalidateInfo();
        }

        public override FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();
            NestedPath.EnsureLeaf(newName);

            PathUtil.ValidateLocalName(newName);

            var parentPath = System.IO.Path.GetDirectoryName(Path);
            var newPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentPath, newName));

            EnsureWithinRoot(newPath);
#if NET8_0_OR_GREATER
            EnsureNoSymlinkEscape(newPath);
#endif

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

        public override FileDirectory MoveTo(FileDirectory directory, string name, bool overwrite = false)
        {
            ThrowIfReadOnly();

            // Same-filesystem move: rename via Directory.Move (atomic) when the
            // destination is a fresh name on the same volume. Directory.Move does
            // NOT span volumes, so a cross-volume (or cross-driver, or merge-into-
            // existing) destination falls back to copy + delete.
            if (directory is LocalDirectory localDir)
            {
                if (NestedPath.HasSeparator(name))
                {
                    if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                        return MoveTo(localDir.CreateDirectory(subPath), leaf, overwrite);
                    name = leaf;
                }

                PathUtil.ValidateLocalName(name);
                var destPath = localDir.ResolveSafeChildPath(name);

                // Moving onto the same physical path is a caller error — refuse it
                // explicitly. Otherwise the copy+delete fallback would copy the
                // directory onto itself and then delete the source (losing it, and
                // an empty source outright). Mirrors File.Move's self-move refusal.
                if (string.Equals(System.IO.Path.GetFullPath(Path), destPath, StringComparison.OrdinalIgnoreCase))
                    throw new FileAlreadyExistsException($"Cannot move directory \"{Path}\" onto itself.", Path);

                // Moving a directory into one of its own descendants would make
                // the copy+delete fallback recurse into the tree it is growing.
                if (IsDescendantPath(destPath))
                    throw new FileHubException($"Cannot move directory \"{Path}\" into one of its descendants.");

                // Only take the atomic path for a clean, non-existing target;
                // merging into an existing directory keeps the copy+delete
                // semantics callers already rely on.
                if (!Directory.Exists(destPath) && !File.Exists(destPath))
                {
                    try
                    {
                        Directory.Move(Path, destPath);
                        InvalidateInfo();
                        return new LocalDirectory(destPath, localDir.RootPath, localDir);
                    }
                    catch (IOException)
                    {
                        // Cross-volume move is unsupported by Directory.Move —
                        // fall through to copy + delete.
                    }
                }
            }

            return MoveByCopyDelete(directory, name, overwrite);
        }

        // Copy the subtree, then delete the source. A delete failure rolls the
        // copy back so no duplicate is left behind, and the raw System.IO error
        // is translated before it reaches the caller.
        private FileDirectory MoveByCopyDelete(FileDirectory directory, string name, bool overwrite)
        {
            var copied = CopyTo(directory, name, overwrite);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Source already gone — move is effectively complete.
            }
            catch (Exception ex)
            {
                try { copied.Delete(recursive: true); } catch { /* best effort rollback */ }
                throw new FileHubException(
                    $"Failed to move directory \"{Path}\" to \"{copied.Path}\"; the partial copy was rolled back.", ex);
            }
            InvalidateInfo();
            return copied;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name, bool overwrite = false)
        {
            if (directory is LocalDirectory localDir)
            {
                var destPath = localDir.ResolveSafeChildPath(name);
                // Copying onto the same physical path is a caller error.
                if (string.Equals(System.IO.Path.GetFullPath(Path), destPath, StringComparison.OrdinalIgnoreCase))
                    throw new FileAlreadyExistsException($"Cannot copy directory \"{Path}\" onto itself.", Path);
                // Copying into a descendant would recurse into the growing tree.
                if (IsDescendantPath(destPath))
                    throw new FileHubException($"Cannot copy directory \"{Path}\" into one of its descendants.");
            }

            // overwrite: false must not clobber an existing destination — throw
            // before anything is copied. overwrite: true merges, replacing
            // colliding leaves.
            if (!overwrite && directory.Exists(name))
                throw new FileAlreadyExistsException(directory.CombineChildPath(name));

            var newDir = directory.CreateDirectory(name);
            CopyContents(this, newDir, overwrite);
            return newDir;
        }

        // True when destFullPath lives strictly beneath this directory's own
        // path — used to reject move/copy of a directory into its own subtree.
        private bool IsDescendantPath(string destFullPath)
        {
            var source = System.IO.Path.GetFullPath(Path);
            var sep = System.IO.Path.DirectorySeparatorChar;
            var sourceWithSep = source.EndsWith(sep.ToString()) ? source : source + sep;
            return destFullPath.StartsWith(sourceWithSep, StringComparison.OrdinalIgnoreCase);
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

        private static void CopyContents(FileDirectory source, FileDirectory destination, bool overwrite)
        {
            foreach (var file in source.GetFiles())
                file.CopyTo(destination, file.Name, progress: null, overwrite: overwrite);

            foreach (var subDir in source.GetDirectories())
            {
                var newSubDir = destination.CreateDirectory(subDir.Name);
                CopyContents(subDir, newSubDir, overwrite);
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
