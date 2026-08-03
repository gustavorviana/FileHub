using FileHub.Ftp.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Ftp
{
    public class FtpDirectory : FileDirectory, IRefreshable
    {
        private readonly IFtpSession _session;
        private readonly FtpDirectory _parent;
        private readonly string _path;
        private readonly string _rootPathFtp;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;

        public override string Path => _path;
        public override FileDirectory Parent => _parent;

        /// <summary>
        /// Cached creation timestamp. Returns <c>default</c> until the first
        /// <see cref="Refresh"/> / <see cref="RefreshAsync"/> populates it.
        /// Drivers do not do hidden I/O inside getters.
        /// </summary>
        public override DateTime CreationTimeUtc => _creationTimeUtc;

        /// <summary>Cached last-write timestamp. See <see cref="CreationTimeUtc"/>.</summary>
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc;

        internal IFtpSession SessionInternal => _session;
        internal string PathInternal => _path;
        internal string RootPathInternal => _rootPathFtp;

        /// <summary>Constructor used for the root directory of a FileHub.</summary>
        internal FtpDirectory(IFtpSession session, string rootPath)
            : base(GetDisplayName(rootPath), rootPath: rootPath)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _path = rootPath ?? "/";
            _rootPathFtp = _path;
            _parent = null;
        }

        /// <summary>Constructor used for child directories.</summary>
        internal FtpDirectory(FtpDirectory parent, string name)
            : base(name, rootPath: parent?.RootPathInternal)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _session = parent._session;
            _rootPathFtp = parent._rootPathFtp;
            _path = FtpPathUtil.Combine(parent._path, name);
        }

        private static string GetDisplayName(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || rootPath == "/")
                return "/";
            return PathUtil.GetLeafName(rootPath);
        }

        // === IRefreshable ===

        public void Refresh() => SyncBridge.Run(RefreshAsync);

        /// <summary>
        /// Re-fetches this directory's metadata from the server. If this is the
        /// hub root and the configured path does not exist yet, the directory
        /// is created server-side as part of the refresh — matching the
        /// "hub at a sandboxed subpath" expectation.
        /// </summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (_parent == null && _path != "/")
            {
                var exists = await _session.Client.DirectoryExistsAsync(_path, cancellationToken).ConfigureAwait(false);
                if (!exists)
                    await _session.Client.CreateDirectoryAsync(_path, recursive: true, cancellationToken).ConfigureAwait(false);
            }

            if (_path == "/")
            {
                _creationTimeUtc = default;
                _lastWriteTimeUtc = default;
                return;
            }

            try
            {
                var info = await _session.Client.StatAsync(_path, cancellationToken).ConfigureAwait(false);
                if (info != null)
                {
                    _creationTimeUtc = info.CreatedUtc == default ? info.ModifiedUtc : info.CreatedUtc;
                    _lastWriteTimeUtc = info.ModifiedUtc;
                }
                else
                {
                    _creationTimeUtc = default;
                    _lastWriteTimeUtc = default;
                }
            }
            catch (FileNotFoundException)
            {
                _creationTimeUtc = default;
                _lastWriteTimeUtc = default;
            }
        }

        // === Existence ===

        public override bool Exists() => SyncBridge.Run(ExistsAsync);

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await _session.Client.DirectoryExistsAsync(_path, cancellationToken).ConfigureAwait(false);
        }

        // === File operations ===

        public override FileEntry CreateFile(string name) => SyncBridge.Run(ct => CreateFileAsync(name, ct));

        public override async Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = OpenOrCreateChildDirectory(head, createIfNotExists: true);
                return await dir.CreateFileAsync(rest, cancellationToken).ConfigureAwait(false);
            }
            PathUtil.ValidateName(head);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (await ExistsAsync(head, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(CombineChildPath(head));

            var fullPath = FtpPathUtil.ResolveSafeChildPath(_rootPathFtp, _path, head);
#if NET8_0_OR_GREATER
            await using (var stream = await _session.Client.OpenWriteAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                // Empty file — the using block disposes the stream and closes
                // the data channel even if the close itself throws.
            }
#else
            using (var stream = await _session.Client.OpenWriteAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
            }
#endif
            return new FtpFile(this, head, length: 0, modifiedUtc: DateTime.UtcNow, createdUtc: DateTime.UtcNow);
        }

        public override bool TryOpenFile(string name, out FileEntry file)
        {
            var result = SyncBridge.Run(ct => TryOpenFileAsync(name, ct));
            file = result.File;
            return result.Exists;
        }

        public override async Task<(FileEntry File, bool Exists)> TryOpenFileAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dirResult = await TryOpenDirectoryAsync(head, cancellationToken).ConfigureAwait(false);
                if (!dirResult.Exists)
                    return (null, false);
                return await dirResult.Directory.TryOpenFileAsync(rest, cancellationToken).ConfigureAwait(false);
            }
            var file = await TryOpenFileCoreAsync(head, cancellationToken).ConfigureAwait(false);
            return (file, file != null);
        }

        private async Task<FileEntry> TryOpenFileCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                PathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var fullPath = FtpPathUtil.Combine(_path, name);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await _session.Client.StatAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (info == null || info.IsDirectory) return null;
                return new FtpFile(this, name, info.Size, info.ModifiedUtc, info.CreatedUtc);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null)
        {
            ValidatePaging(limit);
            return GetFilesIterator(searchPattern, offset, limit);
        }

        private IEnumerable<FileEntry> GetFilesIterator(string searchPattern, FileListOffset offset, int? limit)
        {
            var listing = SyncBridge.Run(async ct =>
            {
                await _session.EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await _session.Client.ListAsync(_path, ct).ConfigureAwait(false);
            });
            foreach (var item in EnumerateFiles(listing, searchPattern, offset, limit))
                yield return item;
        }

        private IEnumerable<FtpFile> EnumerateFiles(IReadOnlyList<FtpItemInfo> listing, string searchPattern, FileListOffset offset, int? limit)
        {
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);

            IEnumerable<FtpItemInfo> filtered = listing
                .Where(i => !i.IsDirectory)
                .Where(i => regex.IsMatch(i.Name))
                .OrderBy(i => i.Name, StringComparer.Ordinal);

            if (offset.IsNamed)
                filtered = filtered.Where(i => string.CompareOrdinal(i.Name, offset.Name) > 0);

            int skipped = 0;
            int yielded = 0;
            foreach (var item in filtered)
            {
                if (!offset.IsNamed && skipped < offset.Index) { skipped++; continue; }
                if (limit.HasValue && yielded >= limit.Value) yield break;
                yielded++;
                yield return new FtpFile(this, item.Name, item.Size, item.ModifiedUtc, item.CreatedUtc);
            }
        }

#if NET8_0_OR_GREATER
        public override async IAsyncEnumerable<FileEntry> GetFilesAsync(
            string searchPattern = "*",
            FileListOffset offset = default,
            int? limit = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ValidatePaging(limit);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var listing = await _session.Client.ListAsync(_path, cancellationToken).ConfigureAwait(false);
            foreach (var item in EnumerateFiles(listing, searchPattern, offset, limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
#endif

        // === Directory operations ===

        // === Directory resolution primitives (base validates the whole path) ===

        // Nullable handle for the internal callers.
        private async Task<FileDirectory> TryOpenDirectoryCoreAsync(string name, CancellationToken cancellationToken = default)
            => (await TryOpenDirectoryAsync(name, cancellationToken).ConfigureAwait(false)).Directory;

        // One recursive MKDIR creates the whole path.
        public override async Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var segments = PathUtil.SplitAndValidateSegments(name);
            var fullPath = BuildNestedPath(segments);
            FtpPathUtil.EnsureWithinRoot(_rootPathFtp, fullPath);

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _session.Client.CreateDirectoryAsync(fullPath, recursive: true, cancellationToken).ConfigureAwait(false);
            return BuildDirectoryChain(segments);
        }

        // One probe proves the whole path exists.
        public override async Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            string[] segments;
            try { segments = PathUtil.SplitAndValidateSegments(name); }
            catch (ArgumentException) { return (null, false); }

            var fullPath = BuildNestedPath(segments);
            FtpPathUtil.EnsureWithinRoot(_rootPathFtp, fullPath);

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false)
                ? (BuildDirectoryChain(segments), true)
                : (null, false);
        }

        private string BuildNestedPath(string[] segments)
        {
            var result = _path;
            foreach (var seg in segments)
                result = FtpPathUtil.Combine(result, seg);
            return result;
        }

        private FtpDirectory BuildDirectoryChain(string[] segments)
        {
            FtpDirectory current = this;
            foreach (var seg in segments)
                current = new FtpDirectory(current, seg);
            return current;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var listing = SyncBridge.Run(async ct =>
            {
                await _session.EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await _session.Client.ListAsync(_path, ct).ConfigureAwait(false);
            });
            foreach (var dir in EnumerateDirectories(listing, searchPattern))
                yield return dir;
        }

        private IEnumerable<FtpDirectory> EnumerateDirectories(IReadOnlyList<FtpItemInfo> listing, string searchPattern)
        {
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
            return listing
                .Where(i => i.IsDirectory)
                .Where(i => regex.IsMatch(i.Name))
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .Select(i => new FtpDirectory(this, i.Name));
        }

#if NET8_0_OR_GREATER
        public override async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var listing = await _session.Client.ListAsync(_path, cancellationToken).ConfigureAwait(false);
            foreach (var dir in EnumerateDirectories(listing, searchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return dir;
            }
        }
#endif

        // === Common ===

        public override bool FileExists(string name) => SyncBridge.Run(ct => FileExistsAsync(name, ct));

        public override async Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is FtpDirectory ftpDir)
                    return await ftpDir.FileExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var fullPath = FtpPathUtil.Combine(_path, head);
            return await _session.Client.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        public override bool DirectoryExists(string name) => SyncBridge.Run(ct => DirectoryExistsAsync(name, ct));

        public override async Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is FtpDirectory ftpDir)
                    return await ftpDir.DirectoryExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var fullPath = FtpPathUtil.Combine(_path, head);
            return await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        public override bool Exists(string name) => SyncBridge.Run(ct => ExistsAsync(name, ct));

        // File-or-directory in ONE request, for any depth. A single STAT
        // (GetObjectInfo) on the full path returns an entry whether it's a file
        // or a directory, and null when nothing is there — so we probe the
        // composed path directly instead of opening each intermediate directory
        // (which would cost one round-trip per segment). Every segment is
        // validated up front (blocks "..", separators, control chars); an
        // invalid name simply doesn't exist.
        public override async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            string[] segments;
            try { segments = PathUtil.SplitAndValidateSegments(name); }
            catch (Exception ex) when (ex is ArgumentException || ex is FileHubException) { return false; }
            if (segments.Length == 0) return false;

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var fullPath = _path;
            foreach (var seg in segments)
                fullPath = FtpPathUtil.Combine(fullPath, seg);
            FtpPathUtil.EnsureWithinRoot(_rootPathFtp, fullPath);

            var info = await _session.Client.StatAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return info != null;
        }

        public override void Delete(bool recursive = false) => SyncBridge.Run(ct => DeleteAsync(recursive, ct));

        public override async Task DeleteAsync(bool recursive = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_path == _rootPathFtp)
                throw new NotSupportedException("Cannot delete the root directory of the FileHub.");

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (!recursive && await AnyChildAsync(_path, cancellationToken).ConfigureAwait(false))
                throw new DirectoryNotEmptyException(Path);

            await _session.Client.DeleteDirectoryAsync(_path, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete(string name, bool recursive = false) => SyncBridge.Run(ct => DeleteAsync(name, recursive, ct));

        public override async Task DeleteAsync(string name, bool recursive = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is FtpDirectory ftpDir)
                {
                    await ftpDir.DeleteAsync(rest, recursive, cancellationToken).ConfigureAwait(false);
                    return;
                }
                throw new FileNotFoundException($"The item \"{name}\" was not found under \"{_path}\".");
            }
            PathUtil.ValidateName(head);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var fullPath = FtpPathUtil.Combine(_path, head);

            if (await _session.Client.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                await _session.Client.DeleteFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                if (!recursive && await AnyChildAsync(fullPath, cancellationToken).ConfigureAwait(false))
                    throw new DirectoryNotEmptyException(CombineChildPath(head));

                await _session.Client.DeleteDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new FileNotFoundException($"The item \"{name}\" was not found under \"{_path}\".");
        }

        // Emptiness probe for the non-recursive delete contract: a directory is
        // empty when a LIST returns no entries.
        private async Task<bool> AnyChildAsync(string path, CancellationToken cancellationToken)
        {
            var items = await _session.Client.ListAsync(path, cancellationToken).ConfigureAwait(false);
            return items.Count > 0;
        }

        public override FileDirectory Rename(string newName) => SyncBridge.Run(ct => RenameAsync(newName, ct));

        public override async Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_parent == null)
                throw new NotSupportedException("Cannot rename the root directory.");

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and move into it.
            if (NestedPath.HasSeparator(newName))
            {
                if (NestedPath.TrySplitLeaf(newName, out var subPath, out var leaf))
                {
                    var targetDir = await _parent.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await MoveToAsync(targetDir, leaf, cancellationToken).ConfigureAwait(false);
                }
                newName = leaf;
            }

            PathUtil.ValidateName(newName);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            // Rename never overwrites — a name already taken is an error.
            if (await _parent.ExistsAsync(newName, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(_parent.Path, newName));

            var destination = FtpPathUtil.ResolveSafeChildPath(_rootPathFtp, _parent._path, newName);
            await _session.Client.RenameAsync(_path, destination, cancellationToken).ConfigureAwait(false);
            return new FtpDirectory(_parent, newName);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, ct));

        public override async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and recurse with the leaf.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                {
                    var deeper = await directory.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await MoveToAsync(deeper, leaf, cancellationToken).ConfigureAwait(false);
                }
                name = leaf;
            }

            if (directory is FtpDirectory ftpDir
                && FtpSessionTarget.SameConnection(ftpDir._session.Client, _session.Client))
            {
                PathUtil.ValidateName(name);
                await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var destination = FtpPathUtil.ResolveSafeChildPath(ftpDir._rootPathFtp, ftpDir._path, name);
                // Same connection + same resolved path means moving onto itself.
                if (string.Equals(destination, _path, StringComparison.Ordinal))
                    throw new FileAlreadyExistsException($"Cannot move directory \"{Path}\" onto itself.", Path);
                if (IsDescendantPath(destination))
                    throw new FileHubException($"Cannot move directory \"{Path}\" into one of its descendants.");
                await _session.Client.RenameAsync(_path, destination, cancellationToken).ConfigureAwait(false);
                return new FtpDirectory(ftpDir, name);
            }

            var newDir = await CopyToAsync(directory, name, overwrite: false, cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteAsync(recursive: true, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // Source already gone — move is effectively complete (mirrors
                // the file-level move so the semantics match across entry types).
            }
            catch (Exception ex)
            {
                throw new PartialMoveException(
                    $"Directory was copied to \"{newDir.Path}\" but the original at \"{Path}\" could not be fully deleted. " +
                    "The move is partial — remove the source manually.",
                    sourcePath: Path,
                    destinationPath: newDir.Path,
                    innerException: ex);
            }
            return newDir;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name, bool overwrite = false)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, overwrite, ct));

        public override async Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            if (directory is FtpDirectory ftpDir
                && FtpSessionTarget.SameConnection(ftpDir._session.Client, _session.Client))
            {
                var destination = FtpPathUtil.ResolveSafeChildPath(ftpDir._rootPathFtp, ftpDir._path, name);
                // Same connection + same resolved path means copying onto itself.
                if (string.Equals(destination, _path, StringComparison.Ordinal))
                    throw new FileAlreadyExistsException($"Cannot copy directory \"{Path}\" onto itself.", Path);
                // Copying into a descendant would recurse into the growing tree.
                if (IsDescendantPath(destination))
                    throw new FileHubException($"Cannot copy directory \"{Path}\" into one of its descendants.");
            }

            // overwrite: false must not clobber an existing destination — throw
            // before anything is copied. overwrite: true merges, replacing
            // colliding leaves.
            if (!overwrite && await directory.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(directory.Path, name));

            // FTP has no server-side copy command, even within the same
            // connection — do a generic recursive copy. Each file leaf streams
            // via FtpFile.CopyToAsync (temp-file spill on the same connection);
            // the whole walk stays async and honors the cancellation token.
            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
            await CopyContentsAsync(this, newDir, overwrite, cancellationToken).ConfigureAwait(false);
            return newDir;
        }

        // === Helpers ===

        // True when destination lives strictly beneath this directory's own path
        // on the same server — used to reject move/copy into its own subtree.
        private bool IsDescendantPath(string destination)
        {
            if (string.IsNullOrEmpty(_path) || _path == "/") return false;
            var prefix = _path.EndsWith("/") ? _path : _path + "/";
            return destination.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static async Task CopyContentsAsync(FileDirectory source, FileDirectory destination, bool overwrite, CancellationToken cancellationToken)
        {
#if NET8_0_OR_GREATER
            await foreach (var file in source.GetFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                await file.CopyToAsync(destination, file.Name, overwrite: overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);
            await foreach (var subDir in source.GetDirectoriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var newSubDir = await destination.CreateDirectoryAsync(subDir.Name, cancellationToken).ConfigureAwait(false);
                await CopyContentsAsync(subDir, newSubDir, overwrite, cancellationToken).ConfigureAwait(false);
            }
#else
            foreach (var file in await source.GetFilesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                await file.CopyToAsync(destination, file.Name, overwrite: overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var subDir in await source.GetDirectoriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var newSubDir = await destination.CreateDirectoryAsync(subDir.Name, cancellationToken).ConfigureAwait(false);
                await CopyContentsAsync(subDir, newSubDir, overwrite, cancellationToken).ConfigureAwait(false);
            }
#endif
        }
    }
}
