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

        // === Abstract async primitives (drivers implement — async is the source of truth) ===

        public abstract Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>Resolve the file; <c>Exists</c> is <c>false</c> and <c>File</c> is <c>null</c> when it doesn't exist.</summary>
        public abstract Task<(FileEntry File, bool Exists)> TryOpenFileAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>Create the directory named <paramref name="name"/> and every missing ancestor.</summary>
        public abstract Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>Resolve the whole path; <c>Exists</c> is <c>false</c> and <c>Directory</c> is <c>null</c> when it doesn't exist.</summary>
        public abstract Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default);

        public abstract Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default);

        public abstract Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default);

        public abstract Task DeleteAsync(CancellationToken cancellationToken = default);

        public abstract Task DeleteAsync(string name, CancellationToken cancellationToken = default);

        // === Abstract enumeration ===
        // Enumeration stays a sync pull model because IAsyncEnumerable is not
        // available on netstandard2.0; the *Async counterparts below are
        // virtual so drivers with native pagination can override them.

        public abstract IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null);

        public abstract IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*");

        // === Sync bridges (delegate to the async source of truth) ===

        public virtual FileEntry CreateFile(string name)
            => SyncBridge.Run(ct => CreateFileAsync(name, ct));

        public virtual bool TryOpenFile(string name, out FileEntry file)
        {
            var (f, exists) = SyncBridge.Run(ct => TryOpenFileAsync(name, ct));
            file = f;
            return exists;
        }

        public virtual FileDirectory CreateDirectory(string name)
            => SyncBridge.Run(ct => CreateDirectoryAsync(name, ct));

        public virtual bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            var (dir, exists) = SyncBridge.Run(ct => TryOpenDirectoryAsync(name, ct));
            directory = dir;
            return exists;
        }

        public virtual bool FileExists(string name)
            => SyncBridge.Run(ct => FileExistsAsync(name, ct));

        public virtual bool DirectoryExists(string name)
            => SyncBridge.Run(ct => DirectoryExistsAsync(name, ct));

        public virtual void Delete()
            => SyncBridge.Run(ct => DeleteAsync(ct));

        public virtual void Delete(string name)
            => SyncBridge.Run(ct => DeleteAsync(name, ct));

        /// <summary>
        /// Returns whether <em>anything</em> — a file or a directory — already
        /// occupies <paramref name="name"/> in this directory. Prefer this over
        /// calling <see cref="FileExistsAsync"/> and <see cref="DirectoryExistsAsync"/>
        /// back to back: the base implementation does exactly that, but the
        /// object-storage drivers (S3, OCI) override it to answer with a single
        /// LIST request instead of two round-trips — the difference matters on a
        /// billed, latency-bound backend.
        /// </summary>
        public virtual async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
            => await FileExistsAsync(name, cancellationToken).ConfigureAwait(false)
               || await DirectoryExistsAsync(name, cancellationToken).ConfigureAwait(false);

        /// <summary>Sync sibling of <see cref="ExistsAsync(string, CancellationToken)"/>.</summary>
        public virtual bool Exists(string name)
            => SyncBridge.Run(ct => ExistsAsync(name, ct));

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
        protected internal virtual string CombineChildPath(string name) => PathUtil.JoinDisplay(Path, name);

        // === Composite operations (async holds the logic; sync bridges) ===

        /// <summary>
        /// Rename this directory under the same parent. Base implementation
        /// delegates to <see cref="MoveToAsync(FileDirectory, string, CancellationToken)"/>
        /// with the same parent — drivers backed by a store that has a native
        /// rename (FTP <c>RNFR/RNTO</c>, OCI same-bucket rename, file-system
        /// <c>Move</c>) override to use it directly.
        /// </summary>
        public virtual async Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (Parent == null)
                throw new InvalidOperationException("Cannot rename the root directory.");
            return await MoveToAsync(Parent, newName, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc cref="RenameAsync(string, CancellationToken)"/>
        public virtual FileDirectory Rename(string newName)
            => SyncBridge.Run(ct => RenameAsync(newName, ct));

        /// <summary>
        /// Move this directory under <paramref name="directory"/> with
        /// <paramref name="name"/>. Base implementation = copy then delete.
        /// Drivers with an atomic move primitive override.
        /// </summary>
        public virtual async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var newDir = await CopyToAsync(directory, name, overwrite: false, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(cancellationToken).ConfigureAwait(false);
            return newDir;
        }

        /// <inheritdoc cref="MoveToAsync(FileDirectory, string, CancellationToken)"/>
        public virtual FileDirectory MoveTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, ct));

        /// <summary>
        /// Recursively copy this directory's contents into a new directory
        /// named <paramref name="name"/> under <paramref name="directory"/>.
        /// Base implementation walks files + subdirectories and copies each
        /// — works across drivers (stream copy on file leaves). Drivers
        /// backed by a store with server-side copy (S3 <c>CopyObject</c>,
        /// OCI <c>CopyObject</c>) override for cheaper bulk copy.
        /// </summary>
        public virtual async Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (directory == null) throw new ArgumentNullException(nameof(directory));

            // overwrite: false must not clobber an existing destination — throw
            // up-front so nothing is half-copied. overwrite: true merges into the
            // destination, replacing colliding leaves.
            if (!overwrite && await directory.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(directory.CombineChildPath(name));

            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
#if NET8_0_OR_GREATER
            await foreach (var file in GetFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                await file.CopyToAsync(newDir, file.Name, overwrite: overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);
            await foreach (var subDir in GetDirectoriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                await subDir.CopyToAsync(newDir, subDir.Name, overwrite, cancellationToken).ConfigureAwait(false);
#else
            foreach (var file in await GetFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                await file.CopyToAsync(newDir, file.Name, overwrite: overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var subDir in await GetDirectoriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                await subDir.CopyToAsync(newDir, subDir.Name, overwrite, cancellationToken).ConfigureAwait(false);
#endif
            return newDir;
        }

        /// <inheritdoc cref="CopyToAsync(FileDirectory, string, bool, CancellationToken)"/>
        public virtual FileDirectory CopyTo(FileDirectory directory, string name, bool overwrite = false)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, overwrite, ct));

        public virtual async Task<FileEntry> CreateFileAsync(string name, bool overwrite, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (overwrite)
                await DeleteIfExistsAsync(name, cancellationToken).ConfigureAwait(false);
            return await CreateFileAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public virtual FileEntry CreateFile(string name, bool overwrite)
            => SyncBridge.Run(ct => CreateFileAsync(name, overwrite, ct));

        /// <summary>
        /// Create a file with initial content and optional metadata applied in
        /// a single call. Base implementation creates an empty file then writes
        /// — drivers may override to fuse the calls (e.g., a single
        /// <c>PutObject</c> on object-storage backends).
        /// </summary>
        public virtual async Task<FileEntry> CreateFileAsync(string name, byte[] bytes, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            cancellationToken.ThrowIfCancellationRequested();
            var file = await CreateFileAsync(name, cancellationToken).ConfigureAwait(false);
            await file.SetBytesAsync(bytes, options, cancellationToken).ConfigureAwait(false);
            return file;
        }

        /// <inheritdoc cref="CreateFileAsync(string, byte[], FileWriteOptions, CancellationToken)"/>
        public virtual FileEntry CreateFile(string name, byte[] bytes, FileWriteOptions options = null)
            => SyncBridge.Run(ct => CreateFileAsync(name, bytes, options, ct));

        public virtual Task<FileEntry> OpenFileAsync(string name, CancellationToken cancellationToken = default)
            => OpenFileAsync(name, createIfNotExists: false, cancellationToken);

        public virtual async Task<FileEntry> OpenFileAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);

            if (rest == null)
            {
                var (file, exists) = await TryOpenFileAsync(head, cancellationToken).ConfigureAwait(false);
                if (exists)
                    return file;

                if (createIfNotExists)
                    return await CreateFileAsync(head, cancellationToken).ConfigureAwait(false);

                throw new FileNotFoundException($"The file \"{CombineChildPath(name)}\" was not found.");
            }

            var dir = await OpenOrCreateChildDirectoryAsync(head, createIfNotExists, cancellationToken).ConfigureAwait(false);
            return await dir.OpenFileAsync(rest, createIfNotExists, cancellationToken).ConfigureAwait(false);
        }

        public virtual FileEntry OpenFile(string name)
            => OpenFile(name, createIfNotExists: false);

        public virtual FileEntry OpenFile(string name, bool createIfNotExists)
            => SyncBridge.Run(ct => OpenFileAsync(name, createIfNotExists, ct));

        public virtual Task<FileDirectory> OpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
            => OpenDirectoryAsync(name, createIfNotExists: false, cancellationToken);

        public virtual async Task<FileDirectory> OpenDirectoryAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);

            var directory = await OpenOrCreateChildDirectoryAsync(head, createIfNotExists, cancellationToken).ConfigureAwait(false);

            if (rest == null)
                return directory;

            return await directory.OpenDirectoryAsync(rest, createIfNotExists, cancellationToken).ConfigureAwait(false);
        }

        public virtual FileDirectory OpenDirectory(string name)
            => OpenDirectory(name, createIfNotExists: false);

        public virtual FileDirectory OpenDirectory(string name, bool createIfNotExists)
            => SyncBridge.Run(ct => OpenDirectoryAsync(name, createIfNotExists, ct));

        protected virtual async Task<FileDirectory> OpenOrCreateChildDirectoryAsync(string segment, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            var (directory, exists) = await TryOpenDirectoryAsync(segment, cancellationToken).ConfigureAwait(false);
            if (exists)
                return directory;

            if (createIfNotExists)
                return await CreateDirectoryAsync(segment, cancellationToken).ConfigureAwait(false);

            throw new DirectoryNotFoundException($"The directory \"{CombineChildPath(segment)}\" was not found.");
        }

        protected virtual FileDirectory OpenOrCreateChildDirectory(string segment, bool createIfNotExists)
            => SyncBridge.Run(ct => OpenOrCreateChildDirectoryAsync(segment, createIfNotExists, ct));

        public virtual async Task DeleteIfExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (await ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                await DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public virtual void DeleteIfExists(string name)
            => SyncBridge.Run(ct => DeleteIfExistsAsync(name, ct));

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

        // === Async enumeration defaults (wrap the sync pull model — drivers with native pagination override) ===

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
