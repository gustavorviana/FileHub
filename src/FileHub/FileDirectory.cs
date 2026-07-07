using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace FileHub
{
    public abstract class FileDirectory : FileSystemEntry
    {
        public abstract FileDirectory Parent { get; }
        protected string RootPath { get; }

        protected FileDirectory(string name, string rootPath) : base(name)
        {
            RootPath = rootPath;
        }

        // === Abstract (drivers implement) ===

        public abstract FileEntry CreateFile(string name);
        public abstract bool TryOpenFile(string name, out FileEntry file);
        public abstract IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null);

        public abstract IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*");

        /// <summary>Create the directory named <paramref name="name"/> and every missing ancestor.</summary>
        public abstract Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>Resolve the whole path; <c>Exists</c> is <c>false</c> and <c>Directory</c> is <c>null</c> when it doesn't exist.</summary>
        public abstract Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default);

        public virtual FileDirectory CreateDirectory(string name)
            => SyncBridge.Run(ct => CreateDirectoryAsync(name, ct));

        public virtual bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            var (dir, exists) = SyncBridge.Run(ct => TryOpenDirectoryAsync(name, ct));
            directory = dir;
            return exists;
        }

        /// <summary>
        /// Paged enumeration of directories. Base implementation applies
        /// <paramref name="offset"/> and <paramref name="limit"/> on top of
        /// <see cref="GetDirectories(string)"/> via LINQ. Drivers backed by a
        /// store that paginates natively (object storage) may override to push
        /// the slice down to the backend.
        /// </summary>
        public virtual IEnumerable<FileDirectory> GetDirectories(string searchPattern, FileListOffset offset, int? limit = null)
        {
            ValidatePaging(limit);
            IEnumerable<FileDirectory> seq = GetDirectories(searchPattern);
            if (offset.IsNamed)
                seq = seq.Where(d => string.CompareOrdinal(d.Name, offset.Name) >= 0);
            else if (offset.Index > 0)
                seq = seq.Skip(offset.Index);
            if (limit.HasValue) seq = seq.Take(limit.Value);
            return seq;
        }

        public abstract bool FileExists(string name);
        public abstract bool DirectoryExists(string name);
        public abstract void Delete();
        public abstract void Delete(string name);

        /// <summary>
        /// Returns whether <em>anything</em> — a file or a directory — already
        /// occupies <paramref name="name"/> in this directory. Prefer this over
        /// calling <see cref="FileExists"/> and <see cref="DirectoryExists"/>
        /// back to back: the base implementation does exactly that, but the
        /// object-storage drivers (S3, OCI) override it to answer with a single
        /// LIST request instead of two round-trips — the difference matters on a
        /// billed, latency-bound backend.
        /// </summary>
        public virtual bool Exists(string name) => FileExists(name) || DirectoryExists(name);

        /// <summary>Async sibling of <see cref="Exists(string)"/>.</summary>
        public virtual async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
            => await FileExistsAsync(name, cancellationToken).ConfigureAwait(false)
               || await DirectoryExistsAsync(name, cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Joins this directory's <see cref="FileSystemEntry.Path"/> with a child
        /// <paramref name="name"/> into the full path a child entry would live at.
        /// Base implementation uses the driver-neutral <c>/</c> separator, which
        /// suits every store whose paths are logical keys (S3, OCI, FTP, memory).
        /// The local filesystem driver overrides this to combine with the OS-native
        /// separator via <see cref="System.IO.Path"/>. It's <c>protected internal</c>
        /// so drivers can override the joining rule while sibling types in this
        /// assembly (e.g. <see cref="FileEntry"/>) can build destination paths for
        /// diagnostics.
        /// </summary>
        protected internal virtual string CombineChildPath(string name) => $"{Path}/{name}";

        // === Default implementations (drivers override for native paths) ===

        /// <summary>
        /// Rename this directory under the same parent. Base implementation
        /// delegates to <see cref="MoveTo(FileDirectory, string)"/> with the
        /// same parent — drivers backed by a store that has a native rename
        /// (FTP <c>RNFR/RNTO</c>, OCI same-bucket rename, file-system <c>Move</c>)
        /// override to use it directly.
        /// </summary>
        public virtual FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();
            if (Parent == null)
                throw new InvalidOperationException("Cannot rename the root directory.");
            return MoveTo(Parent, newName);
        }

        /// <summary>
        /// Move this directory under <paramref name="directory"/> with
        /// <paramref name="name"/>. Base implementation = copy then delete.
        /// Drivers with an atomic move primitive override.
        /// </summary>
        public virtual FileDirectory MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();
            var newDir = CopyTo(directory, name);
            Delete();
            return newDir;
        }

        /// <summary>
        /// Recursively copy this directory's contents into a new directory
        /// named <paramref name="name"/> under <paramref name="directory"/>.
        /// Base implementation walks files + subdirectories and copies each
        /// — works across drivers (stream copy on file leaves). Drivers
        /// backed by a store with server-side copy (S3 <c>CopyObject</c>,
        /// OCI <c>CopyObject</c>) override for cheaper bulk copy.
        /// </summary>
        public virtual FileDirectory CopyTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();
            if (directory == null) throw new ArgumentNullException(nameof(directory));

            var newDir = directory.CreateDirectory(name);
            foreach (var file in GetFiles())
                file.CopyTo(newDir, file.Name);
            foreach (var subDir in GetDirectories())
                subDir.CopyTo(newDir, subDir.Name);
            return newDir;
        }

        // === Sync default implementations ===

        public FileEntry CreateFile(string name, bool overwrite)
        {
            ThrowIfReadOnly();
            if (overwrite) DeleteIfExists(name);
            return CreateFile(name);
        }

        /// <summary>
        /// Create a file with initial content and optional metadata applied in
        /// a single call. Base implementation creates an empty file then writes
        /// — drivers may override to fuse the calls (e.g., a single
        /// <c>PutObject</c> on object-storage backends).
        /// </summary>
        public virtual FileEntry CreateFile(string name, byte[] bytes, FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var file = CreateFile(name);
            file.SetBytes(bytes, options);
            return file;
        }

        public virtual FileEntry OpenFile(string name)
        {
            return OpenFile(name, createIfNotExists: false);
        }

        public virtual FileEntry OpenFile(string name, bool createIfNotExists)
        {
            var (head, rest) = SplitPath(name);

            if (rest == null)
            {
                if (TryOpenFile(head, out var file))
                    return file;

                if (createIfNotExists)
                    return CreateFile(head);

                throw new FileNotFoundException($"The file \"{System.IO.Path.Combine(Path, name)}\" was not found.");
            }

            var dir = OpenOrCreateChildDirectory(head, createIfNotExists);
            return dir.OpenFile(rest, createIfNotExists);
        }

        public FileDirectory OpenDirectory(string name)
        {
            return OpenDirectory(name, createIfNotExists: false);
        }

        public FileDirectory OpenDirectory(string name, bool createIfNotExists)
        {
            var (head, rest) = SplitPath(name);

            var directory = OpenOrCreateChildDirectory(head, createIfNotExists);

            if (rest == null)
                return directory;

            return directory.OpenDirectory(rest, createIfNotExists);
        }

        protected virtual FileDirectory OpenOrCreateChildDirectory(string segment, bool createIfNotExists)
        {
            if (TryOpenDirectory(segment, out var directory))
                return directory;

            if (createIfNotExists)
                return CreateDirectory(segment);

            throw new DirectoryNotFoundException($"The directory \"{System.IO.Path.Combine(Path, segment)}\" was not found.");
        }

        protected static (string Head, string Remainder) SplitPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));

            if (path[0] == '/' || path[0] == '\\')
                throw new FileHubException($"Absolute paths are not allowed; path \"{path}\" must be relative.");

            // Strip trailing separators so "foo/" and "foo\\" collapse to a
            // single-segment head with null remainder.
            var trimmed = path.TrimEnd('/', '\\');
            if (trimmed.Length == 0)
                throw new FileHubException($"Absolute paths are not allowed; path \"{path}\" must be relative.");

            var idx = trimmed.IndexOfAny(new[] { '/', '\\' });

            string head;
            string remainder;
            if (idx < 0)
            {
                head = trimmed;
                remainder = null;
            }
            else
            {
                head = trimmed.Substring(0, idx);
                var rest = trimmed.Substring(idx + 1).Trim('/', '\\');
                remainder = rest.Length == 0 ? null : rest;
            }

            if (head == "..")
                throw new FileHubException($"Parent-directory traversal is not allowed in path \"{path}\".");

            return (head, remainder);
        }

        public virtual void DeleteIfExists(string name)
        {
            ThrowIfReadOnly();
            if (FileExists(name) || DirectoryExists(name))
                Delete(name);
        }

        // === Async defaults (wrap sync - cloud drivers override) ===

        public virtual Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFile(name));
        }

        public virtual Task<FileEntry> CreateFileAsync(string name, bool overwrite, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFile(name, overwrite));
        }

        /// <inheritdoc cref="CreateFile(string, byte[], FileWriteOptions)"/>
        public virtual async Task<FileEntry> CreateFileAsync(string name, byte[] bytes, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            cancellationToken.ThrowIfCancellationRequested();
            var file = await CreateFileAsync(name, cancellationToken).ConfigureAwait(false);
            await file.SetBytesAsync(bytes, options, cancellationToken).ConfigureAwait(false);
            return file;
        }

        public virtual Task<FileEntry> OpenFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name));
        }

        public virtual Task<FileEntry> OpenFileAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name, createIfNotExists));
        }

        public virtual Task<(FileEntry File, bool Exists)> TryOpenFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = TryOpenFile(name, out var file);
            return Task.FromResult((file, ok));
        }

        public virtual Task<FileDirectory> OpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenDirectory(name));
        }

        public virtual Task<FileDirectory> OpenDirectoryAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenDirectory(name, createIfNotExists));
        }

        public virtual Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(FileExists(name));
        }

        public virtual Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DirectoryExists(name));
        }

        public virtual Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete();
            return Task.CompletedTask;
        }

        public virtual Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(name);
            return Task.CompletedTask;
        }

        public virtual Task DeleteIfExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(name);
            return Task.CompletedTask;
        }

        public virtual Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Rename(newName));
        }

        public virtual Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MoveTo(directory, name));
        }

        public virtual Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CopyTo(directory, name));
        }

#if NET8_0_OR_GREATER
        public virtual async IAsyncEnumerable<FileEntry> GetFilesAsync(
            string searchPattern = "*",
            FileListOffset offset = default,
            int? limit = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var file in GetFiles(searchPattern, offset, limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public virtual async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var dir in GetDirectories(searchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return dir;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Paged async enumeration of directories. Drivers backed by a store
        /// that paginates natively may override.
        /// </summary>
        public virtual async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern,
            FileListOffset offset,
            int? limit = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var dir in GetDirectories(searchPattern, offset, limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return dir;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }
#else
        public virtual Task<IEnumerable<FileEntry>> GetFilesAsync(string searchPattern = "*", FileListOffset offset = default, int? limit = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetFiles(searchPattern, offset, limit));
        }

        public virtual Task<IEnumerable<FileDirectory>> GetDirectoriesAsync(string searchPattern = "*", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetDirectories(searchPattern));
        }

        public virtual Task<IEnumerable<FileDirectory>> GetDirectoriesAsync(string searchPattern, FileListOffset offset, int? limit = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetDirectories(searchPattern, offset, limit));
        }
#endif

        // === Helpers ===

        protected static void ValidatePaging(int? limit)
        {
            if (limit.HasValue && limit.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be non-negative.");
        }

        protected string ResolveSafePath(string relativePath)
        {
            if (string.IsNullOrEmpty(RootPath))
                return relativePath;

            var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
            EnsureWithinRoot(fullPath);
#if NET8_0_OR_GREATER
            EnsureNoSymlinkEscape(fullPath);
#endif
            return fullPath;
        }

        protected void EnsureWithinRoot(string fullPath)
        {
            if (string.IsNullOrEmpty(RootPath)) return;

            var normalizedRoot = System.IO.Path.GetFullPath(RootPath);
            var separator = System.IO.Path.DirectorySeparatorChar;
            var rootWithSep = normalizedRoot.EndsWith(separator.ToString())
                ? normalizedRoot
                : normalizedRoot + separator;

            bool isRoot = string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
            bool isInside = fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

            if (!isRoot && !isInside)
                throw new FileHubException($"Access denied: path \"{fullPath}\" is outside the root directory.");
        }

#if NET8_0_OR_GREATER
        protected void EnsureNoSymlinkEscape(string fullPath)
        {
            if (string.IsNullOrEmpty(RootPath)) return;

            FileSystemInfo info = null;
            if (System.IO.File.Exists(fullPath))
                info = new System.IO.FileInfo(fullPath);
            else if (System.IO.Directory.Exists(fullPath))
                info = new System.IO.DirectoryInfo(fullPath);

            if (info?.LinkTarget == null) return;

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved == null) return;

            EnsureWithinRoot(resolved.FullName);
        }
#endif

        public override string ToString() => Path;
    }
}
