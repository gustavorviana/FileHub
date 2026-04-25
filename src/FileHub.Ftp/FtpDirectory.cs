using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileHub.Ftp.Internal;

namespace FileHub.Ftp
{
    public class FtpDirectory : FileDirectory, IRefreshable
    {
        private readonly IFtpSession _session;
        private readonly FtpDirectory _parent;
        private readonly string _path;
        private readonly string _rootPathFtp;
        private readonly DirectoryPathMode _pathMode;
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
        internal FtpDirectory(IFtpSession session, string rootPath, DirectoryPathMode pathMode)
            : base(GetDisplayName(rootPath), rootPath: rootPath)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _path = rootPath ?? "/";
            _rootPathFtp = _path;
            _parent = null;
            _pathMode = pathMode;
        }

        /// <summary>Constructor used for child directories.</summary>
        internal FtpDirectory(FtpDirectory parent, string name)
            : base(name, rootPath: parent?.RootPathInternal)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _session = parent._session;
            _rootPathFtp = parent._rootPathFtp;
            _path = FtpPathUtil.Combine(parent._path, name);
            _pathMode = parent._pathMode;
        }

        private static string GetDisplayName(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || rootPath == "/")
                return "/";
            return FtpPathUtil.GetLeafName(rootPath);
        }

        // === IRefreshable ===

        public void Refresh() => RefreshAsync().GetAwaiter().GetResult();

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

        public override bool Exists() => ExistsAsync().GetAwaiter().GetResult();

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await _session.Client.DirectoryExistsAsync(_path, cancellationToken).ConfigureAwait(false);
        }

        // === File operations ===

        public override FileEntry CreateFile(string name) => CreateFileAsync(name).GetAwaiter().GetResult();

        public override async Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = OpenOrCreateChildDirectory(head, createIfNotExists: true);
                return await dir.CreateFileAsync(rest, cancellationToken).ConfigureAwait(false);
            }
            FtpPathUtil.ValidateName(head);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

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
            var result = TryOpenFileAsync(name).GetAwaiter().GetResult();
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
                FtpPathUtil.ValidateName(name);
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
            _session.EnsureConnectedAsync(CancellationToken.None).GetAwaiter().GetResult();
            var listing = _session.Client.ListAsync(_path, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var item in EnumerateFiles(listing, searchPattern, offset, limit))
                yield return item;
        }

        private IEnumerable<FtpFile> EnumerateFiles(IReadOnlyList<FtpItemInfo> listing, string searchPattern, FileListOffset offset, int? limit)
        {
            var regex = FtpPathUtil.BuildSearchPatternRegex(searchPattern);

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

        public override FileDirectory CreateDirectory(string name) => CreateDirectoryAsync(name).GetAwaiter().GetResult();

        public override async Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (_pathMode == DirectoryPathMode.Direct)
                    return await CreateDirectoryDirectAsync(name, cancellationToken).ConfigureAwait(false);

                var existing = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                var intermediate = existing
                    ?? await CreateDirectoryAsync(head, cancellationToken).ConfigureAwait(false);
                return await intermediate.CreateDirectoryAsync(rest, cancellationToken).ConfigureAwait(false);
            }

            FtpPathUtil.ValidateName(name);
            var fullPath = FtpPathUtil.ResolveSafeChildPath(_rootPathFtp, _path, name);
            await _session.Client.CreateDirectoryAsync(fullPath, recursive: false, cancellationToken).ConfigureAwait(false);
            return new FtpDirectory(this, name);
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            directory = TryOpenDirectoryCoreAsync(name).GetAwaiter().GetResult();
            return directory != null;
        }

        public override async Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            var dir = await TryOpenDirectoryCoreAsync(name, cancellationToken).ConfigureAwait(false);
            return (dir, dir != null);
        }

        private async Task<FileDirectory> TryOpenDirectoryCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (_pathMode == DirectoryPathMode.Direct)
                    return await TryOpenDirectoryDirectAsync(name, cancellationToken).ConfigureAwait(false);

                var childResult = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (childResult is FtpDirectory ftpChild)
                    return await ftpChild.TryOpenDirectoryCoreAsync(rest, cancellationToken).ConfigureAwait(false);
                return null;
            }

            try
            {
                FtpPathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var fullPath = FtpPathUtil.Combine(_path, name);
            var exists = await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return exists ? new FtpDirectory(this, name) : null;
        }

        // --- Direct-mode implementations: one MKDIR / one CWD-style probe ---

        private async Task<FileDirectory> CreateDirectoryDirectAsync(string nestedName, CancellationToken cancellationToken)
        {
            var segments = ValidateAndSplitNestedSegments(nestedName);
            var fullPath = BuildNestedPath(segments);
            FtpPathUtil.EnsureWithinRoot(_rootPathFtp, fullPath);

            await _session.Client.CreateDirectoryAsync(fullPath, recursive: true, cancellationToken).ConfigureAwait(false);
            return BuildDirectoryChain(segments);
        }

        private async Task<FileDirectory> TryOpenDirectoryDirectAsync(string nestedName, CancellationToken cancellationToken)
        {
            string[] segments;
            try
            {
                segments = ValidateAndSplitNestedSegments(nestedName);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var fullPath = BuildNestedPath(segments);
            FtpPathUtil.EnsureWithinRoot(_rootPathFtp, fullPath);

            var exists = await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return exists ? BuildDirectoryChain(segments) : null;
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
                FtpPathUtil.ValidateName(seg);
            }
            return segments;
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
            _session.EnsureConnectedAsync(CancellationToken.None).GetAwaiter().GetResult();
            var listing = _session.Client.ListAsync(_path, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var dir in EnumerateDirectories(listing, searchPattern))
                yield return dir;
        }

        private IEnumerable<FtpDirectory> EnumerateDirectories(IReadOnlyList<FtpItemInfo> listing, string searchPattern)
        {
            var regex = FtpPathUtil.BuildSearchPatternRegex(searchPattern);
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

        public override bool FileExists(string name) => FileExistsAsync(name).GetAwaiter().GetResult();

        public override async Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            try { FtpPathUtil.ValidateName(name); } catch (ArgumentException) { return false; }

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var fullPath = FtpPathUtil.Combine(_path, name);
            return await _session.Client.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        public override bool DirectoryExists(string name) => DirectoryExistsAsync(name).GetAwaiter().GetResult();

        public override async Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            try { FtpPathUtil.ValidateName(name); } catch (ArgumentException) { return false; }

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var fullPath = FtpPathUtil.Combine(_path, name);
            return await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete() => DeleteAsync().GetAwaiter().GetResult();

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_path == _rootPathFtp)
                throw new NotSupportedException("Cannot delete the root directory of the FileHub.");

            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _session.Client.DeleteDirectoryAsync(_path, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete(string name) => DeleteAsync(name).GetAwaiter().GetResult();

        public override async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            FtpPathUtil.ValidateName(name);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var fullPath = FtpPathUtil.Combine(_path, name);

            if (await _session.Client.FileExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                await _session.Client.DeleteFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (await _session.Client.DirectoryExistsAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                await _session.Client.DeleteDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new FileNotFoundException($"The item \"{name}\" was not found under \"{_path}\".");
        }

        public override FileDirectory Rename(string newName) => RenameAsync(newName).GetAwaiter().GetResult();

        public override async Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_parent == null)
                throw new NotSupportedException("Cannot rename the root directory.");

            FtpPathUtil.ValidateName(newName);
            await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var destination = FtpPathUtil.ResolveSafeChildPath(_rootPathFtp, _parent._path, newName);
            await _session.Client.RenameAsync(_path, destination, cancellationToken).ConfigureAwait(false);
            return new FtpDirectory(_parent, newName);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
            => MoveToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            if (directory is FtpDirectory ftpDir
                && FtpSessionTarget.SameConnection(ftpDir._session.Client, _session.Client))
            {
                FtpPathUtil.ValidateName(name);
                await _session.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var destination = FtpPathUtil.ResolveSafeChildPath(ftpDir._rootPathFtp, ftpDir._path, name);
                await _session.Client.RenameAsync(_path, destination, cancellationToken).ConfigureAwait(false);
                return new FtpDirectory(ftpDir, name);
            }

            var newDir = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteAsync(cancellationToken).ConfigureAwait(false);
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

        public override FileDirectory CopyTo(FileDirectory directory, string name)
            => CopyToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            // FTP has no server-side copy command, even within the same
            // connection — fall through to a generic recursive copy.
            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
            CopyContentsGeneric(this, newDir);
            return newDir;
        }

        // === Helpers ===

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
    }
}
