using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage
{
    public class OracleObjectStorageDirectory : FileDirectory, IRefreshable
    {
        private const string DirectoryContentType = "application/x-directory";

        private readonly IOciSession _session;
        private readonly OracleObjectStorageDirectory _parent;
        private readonly string _prefix;
        private readonly string _rootPrefix;
        private readonly DirectoryPathMode _pathMode;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;

        public override string Path => OciPathUtil.DisplayPath(_prefix);
        public override FileDirectory Parent => _parent;

        /// <summary>
        /// Cached creation timestamp. Returns <c>default</c> until the first
        /// <see cref="Refresh"/> / <see cref="RefreshAsync"/> populates it.
        /// Drivers do not do hidden I/O inside getters.
        /// </summary>
        public override DateTime CreationTimeUtc => _creationTimeUtc;

        /// <summary>Cached last-write timestamp. See <see cref="CreationTimeUtc"/>.</summary>
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc;

        internal IOciSession SessionInternal => _session;
        internal string PrefixInternal => _prefix;
        internal string RootPrefixInternal => _rootPrefix;

        /// <summary>Constructor used for the root directory of a FileHub.</summary>
        internal OracleObjectStorageDirectory(IOciSession session, string rootPrefix)
            : this(session, rootPrefix, DirectoryPathMode.Direct) { }

        /// <summary>Constructor used for the root directory of a FileHub.</summary>
        internal OracleObjectStorageDirectory(IOciSession session, string rootPrefix, DirectoryPathMode pathMode)
            : base(GetDisplayName(rootPrefix), rootPath: rootPrefix)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prefix = rootPrefix ?? string.Empty;
            _rootPrefix = _prefix;
            _parent = null;
            _pathMode = pathMode;
        }

        /// <summary>Constructor used for child directories.</summary>
        internal OracleObjectStorageDirectory(OracleObjectStorageDirectory parent, string name)
            : base(name, rootPath: parent?.RootPrefixInternal)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _session = parent._session;
            _rootPrefix = parent._rootPrefix;
            _prefix = OciPathUtil.CombinePrefix(parent._prefix, name);
            _pathMode = parent._pathMode;
        }

        private static string GetDisplayName(string rootPrefix)
        {
            if (string.IsNullOrEmpty(rootPrefix))
                return "/";
            return OciPathUtil.GetLeafName(rootPrefix);
        }

        // === IRefreshable ===

        public void Refresh() => SyncBridge.Run(ct => RefreshAsync(ct));

        /// <summary>
        /// Re-fetches this directory's metadata from OCI. If this is the hub
        /// root and the configured prefix does not have a marker yet, the
        /// marker object is created as part of the refresh — matching the
        /// "hub scoped to a sandboxed prefix" expectation.
        /// </summary>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(_prefix))
            {
                _creationTimeUtc = default;
                _lastWriteTimeUtc = default;
                return;
            }

            try
            {
                var head = await _session.Client.HeadObjectAsync(_prefix, cancellationToken).ConfigureAwait(false);
                _creationTimeUtc = head.LastModified ?? default;
                _lastWriteTimeUtc = _creationTimeUtc;
            }
            catch (FileNotFoundException)
            {
                if (_parent == null)
                {
                    // Hub root with a configured prefix: create the marker and adopt "now".
                    await PutMarker(cancellationToken).ConfigureAwait(false);
                    _creationTimeUtc = DateTime.UtcNow;
                    _lastWriteTimeUtc = _creationTimeUtc;
                }
                else
                {
                    _creationTimeUtc = default;
                    _lastWriteTimeUtc = default;
                }
            }
        }

        private async Task PutMarker(CancellationToken cancellationToken = default)
        {
            using var empty = new MemoryStream();
            await _session.Client.PutObjectAsync(
                _prefix, empty, contentLength: 0, contentType: DirectoryContentType, opcMeta: null, cancellationToken).ConfigureAwait(false);
        }

        // === Existence ===

        public override bool Exists() => SyncBridge.Run(ct => ExistsAsync(ct));

        public override Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            // Single LIST(limit=1) covers marker-backed and implicit prefixes.
            return AnyObjectUnderPrefixAsync(_prefix, cancellationToken);
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
            var objectName = OciPathUtil.ResolveSafeObjectName(_rootPrefix, _prefix, head);
            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(objectName, empty, 0, null, null, cancellationToken).ConfigureAwait(false);
            }
            var created = new OracleObjectStorageFile(this, head, 0, DateTime.UtcNow);
            created.MarkLoaded();
            return created;
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

        // === OpenFile override: zero-call stub for createIfNotExists ===
        //
        // Strict (createIfNotExists == false): fall through to base, which
        // calls TryOpenFile → 1 × HEAD → loaded file or FileNotFoundException.
        //
        // createIfNotExists == true: RETURN STUB. No HEAD, no PutObject.
        // OCI is pay-per-request; deferring creation to the first write saves
        // round-trips. Caller's responsibility: write to materialize, read
        // fails with FileNotFoundException if the object doesn't exist.

        public override FileEntry OpenFile(string name, bool createIfNotExists)
        {
            if (!createIfNotExists) return base.OpenFile(name, createIfNotExists);

            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = OpenOrCreateChildDirectory(head, createIfNotExists: true);
                return dir.OpenFile(rest, createIfNotExists: true);
            }
            OciPathUtil.ValidateName(head);
            return new OracleObjectStorageFile(this, head);   // stub, IsLoaded = false
        }

        public override Task<FileEntry> OpenFileAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            if (!createIfNotExists) return base.OpenFileAsync(name, createIfNotExists, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name, createIfNotExists: true));
        }

        // OCI "directories" are only name prefixes — there is no real
        // container entity. When the caller signals `createIfNotExists: true`
        // we don't need to HEAD the marker, LIST children, nor PUT an empty
        // marker: the prefix is implicitly usable the moment a child object
        // is written. Strict (false) keeps the base semantics so missing
        // paths still throw DirectoryNotFoundException.
        protected override FileDirectory OpenOrCreateChildDirectory(string segment, bool createIfNotExists)
        {
            if (createIfNotExists)
            {
                OciPathUtil.ValidateName(segment);
                return new OracleObjectStorageDirectory(this, segment);
            }
            return base.OpenOrCreateChildDirectory(segment, createIfNotExists);
        }

        private async Task<FileEntry> TryOpenFileCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                OciPathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var objectName = OciPathUtil.CombineObjectName(_prefix, name);
            try
            {
                var head = await _session.Client.HeadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                var file = new OracleObjectStorageFile(this, name, head.ContentLength ?? 0, head.LastModified);
                file.MarkLoaded();
                return file;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Lists files under this prefix, optionally paginated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Named offsets (<see cref="FileListOffset.FromName(string)"/>)</b>
        /// are pushed straight into OCI's <c>start</c> parameter — a single
        /// round-trip gets you to the cursor regardless of how many objects
        /// precede it. This is the recommended way to paginate large
        /// listings.
        /// </para>
        /// <para>
        /// <b>Index offsets (<see cref="FileListOffset.FromIndex(int)"/>) are
        /// expensive on OCI</b>: the protocol has no "skip N" primitive, so
        /// the driver walks every preceding object client-side until the
        /// index is reached. Cost grows linearly with the offset (API calls,
        /// bandwidth and latency), and on very large buckets this can be
        /// ruinous. Avoid index offsets for anything beyond small directories
        /// — prefer named offsets derived from the last item of the previous
        /// page.
        /// </para>
        /// </remarks>
        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null)
        {
            ValidatePaging(limit);
            return GetFilesIterator(searchPattern, offset, limit);
        }

        private IEnumerable<FileEntry> GetFilesIterator(string searchPattern, FileListOffset offset, int? limit)
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            int? backendLimit = ResolveBackendLimit(offset, limit);
            string start = offset.IsNamed ? _prefix + offset.Name : null;
            int skipped = 0;
            int yielded = 0;
            do
            {
                var page = SyncBridge.Run(ct => _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: backendLimit, start: start, ct));
                foreach (var obj in page.Objects)
                {
                    if (!IsChildFile(obj.Name, out var leaf)) continue;
                    if (!regex.IsMatch(leaf)) continue;
                    if (!offset.IsNamed && skipped < offset.Index) { skipped++; continue; }
                    if (limit.HasValue && yielded >= limit.Value) yield break;
                    yielded++;
                    yield return new OracleObjectStorageFile(this, leaf, obj.Size ?? 0, obj.TimeCreated);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Asynchronously lists files under this prefix, optionally paginated.
        /// </summary>
        /// <remarks>
        /// Same cost model as the sync <see cref="GetFiles"/>: named offsets
        /// ride on OCI's <c>start</c> parameter (cheap), index offsets require
        /// a client-side walk over every preceding object (expensive — avoid
        /// on large buckets).
        /// </remarks>
        public override async IAsyncEnumerable<FileEntry> GetFilesAsync(
            string searchPattern = "*",
            FileListOffset offset = default,
            int? limit = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ValidatePaging(limit);
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            int? backendLimit = ResolveBackendLimit(offset, limit);
            string start = offset.IsNamed ? _prefix + offset.Name : null;
            int skipped = 0;
            int yielded = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: backendLimit, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    if (!IsChildFile(obj.Name, out var leaf)) continue;
                    if (!regex.IsMatch(leaf)) continue;
                    if (!offset.IsNamed && skipped < offset.Index) { skipped++; continue; }
                    if (limit.HasValue && yielded >= limit.Value) yield break;
                    yielded++;
                    yield return new OracleObjectStorageFile(this, leaf, obj.Size ?? 0, obj.TimeCreated);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }
#endif

        private static int? ResolveBackendLimit(FileListOffset offset, int? limit)
        {
            if (!limit.HasValue) return null;
            if (offset.IsNamed)
                return limit.Value < 1000 ? limit : null;
            long total = (long)offset.Index + limit.Value;
            return total < 1000 ? (int)total : null;
        }

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name) => SyncBridge.Run(ct => CreateDirectoryAsync(name, ct));

        public override async Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (_pathMode == DirectoryPathMode.Direct)
                    return await CreateDirectoryDirectAsync(name, cancellationToken).ConfigureAwait(false);

                var existing = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                var intermediate = existing
                    ?? await CreateDirectoryAsync(head, cancellationToken).ConfigureAwait(false);
                return await intermediate.CreateDirectoryAsync(rest, cancellationToken).ConfigureAwait(false);
            }

            var childPrefix = OciPathUtil.ResolveSafeChildPrefix(_rootPrefix, _prefix, name);

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(childPrefix, empty, 0, DirectoryContentType, null, cancellationToken).ConfigureAwait(false);
            }
            return new OracleObjectStorageDirectory(this, name);
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            directory = SyncBridge.Run(ct => TryOpenDirectoryCoreAsync(name, ct));
            return directory != null;
        }

        public override async Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            var dir = await TryOpenDirectoryCoreAsync(name, cancellationToken).ConfigureAwait(false);
            return (dir, dir != null);
        }

        private async Task<FileDirectory> TryOpenDirectoryCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            if (NestedPath.TrySplit(name, out var head, out var rest))
            {
                if (_pathMode == DirectoryPathMode.Direct)
                    return await TryOpenDirectoryDirectAsync(name, cancellationToken).ConfigureAwait(false);

                var childResult = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (childResult is OracleObjectStorageDirectory ociChild)
                    return await ociChild.TryOpenDirectoryCoreAsync(rest, cancellationToken).ConfigureAwait(false);
                return null;
            }

            try
            {
                OciPathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);
            if (await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
                return new OracleObjectStorageDirectory(this, name);
            return null;
        }

        // --- Direct-mode implementations: one PUT / one HEAD for the nested leaf ---

        private async Task<FileDirectory> CreateDirectoryDirectAsync(string nestedName, CancellationToken cancellationToken)
        {
            var segments = ValidateAndSplitNestedSegments(nestedName);
            var fullPrefix = BuildNestedPrefix(segments);
            OciPathUtil.EnsureWithinRootPrefix(_rootPrefix, fullPrefix);

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(fullPrefix, empty, 0, DirectoryContentType, null, cancellationToken).ConfigureAwait(false);
            }
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
            var fullPrefix = BuildNestedPrefix(segments);
            OciPathUtil.EnsureWithinRootPrefix(_rootPrefix, fullPrefix);

            if (await AnyObjectUnderPrefixAsync(fullPrefix, cancellationToken).ConfigureAwait(false))
                return BuildDirectoryChain(segments);
            return null;
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
                OciPathUtil.ValidateName(seg);
            }
            return segments;
        }

        private string BuildNestedPrefix(string[] segments)
        {
            var result = _prefix ?? string.Empty;
            foreach (var seg in segments)
                result += seg + "/";
            return result;
        }

        private OracleObjectStorageDirectory BuildDirectoryChain(string[] segments)
        {
            OracleObjectStorageDirectory current = this;
            foreach (var seg in segments)
                current = new OracleObjectStorageDirectory(current, seg);
            return current;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                var page = SyncBridge.Run(ct => _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start, ct));
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = OciPathUtil.GetLeafName(childPrefix);
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new OracleObjectStorageDirectory(this, leaf);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }

#if NET8_0_OR_GREATER
        public override async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var regex = OciPathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = OciPathUtil.GetLeafName(childPrefix);
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new OracleObjectStorageDirectory(this, leaf);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));
        }
#endif

        // === Common ===

        public override bool FileExists(string name) => SyncBridge.Run(ct => FileExistsAsync(name, ct));

        public override async Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            try { OciPathUtil.ValidateName(name); } catch (ArgumentException) { return false; }
            var objectName = OciPathUtil.CombineObjectName(_prefix, name);
            try
            {
                await _session.Client.HeadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        public override bool DirectoryExists(string name) => SyncBridge.Run(ct => DirectoryExistsAsync(name, ct));

        // LIST(prefix, limit=1) covers both cases in one call: explicit "/"
        // marker or any implicit child. HEAD-first was only cheaper when
        // markers were the norm; we no longer auto-create them on nested
        // writes, so LIST is the single probe that pays off.
        public override async Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            try { OciPathUtil.ValidateName(name); } catch (ArgumentException) { return false; }
            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);
            return await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete() => SyncBridge.Run(ct => DeleteAsync(ct));

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_prefix == _rootPrefix)
                throw new NotSupportedException("Cannot delete the root directory of the FileHub.");

            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete(string name) => SyncBridge.Run(ct => DeleteAsync(name, ct));

        public override async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            OciPathUtil.ValidateName(name);

            var objectName = OciPathUtil.CombineObjectName(_prefix, name);
            try
            {
                await _session.Client.DeleteObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (FileNotFoundException)
            {
                // fall through to directory delete attempt
            }

            var childPrefix = OciPathUtil.CombinePrefix(_prefix, name);
            if (await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
            {
                await DeleteAllUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new FileNotFoundException($"The item \"{name}\" was not found under \"{Path}\".");
        }

        public override FileDirectory Rename(string newName) => SyncBridge.Run(ct => RenameAsync(newName, ct));

        public override async Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_parent == null)
                throw new NotSupportedException("Cannot rename the root directory.");

            OciPathUtil.ValidateName(newName);
            var destinationPrefix = OciPathUtil.CombinePrefix(_parent._prefix, newName);
            await CopyAllObjectsAsync(_prefix, _session.Client, destinationPrefix, cancellationToken).ConfigureAwait(false);
            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            return new OracleObjectStorageDirectory(_parent, newName);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, ct));

        public override async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var newDir = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            return newDir;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, ct));

        public override async Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            if (directory is OracleObjectStorageDirectory ociDir
                && OciSessionTarget.SameCredentials(ociDir._session.Client, _session.Client))
            {
                OciPathUtil.ResolveSafeChildPrefix(ociDir._rootPrefix, ociDir._prefix, name);
                var destinationPrefix = OciPathUtil.CombinePrefix(ociDir._prefix, name);
                await CopyAllObjectsAsync(_prefix, ociDir._session.Client, destinationPrefix, cancellationToken).ConfigureAwait(false);
                return new OracleObjectStorageDirectory(ociDir, name);
            }

            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
            CopyContentsGeneric(this, newDir);
            return newDir;
        }

        // === Helpers ===

        private bool IsChildFile(string objectName, out string leaf)
        {
            leaf = null;
            if (!objectName.StartsWith(_prefix, StringComparison.Ordinal)) return false;
            if (objectName.Length == _prefix.Length) return false; // own marker
            var rest = objectName.Substring(_prefix.Length);
            if (rest.EndsWith("/", StringComparison.Ordinal)) return false; // subdir marker
            if (rest.IndexOf('/') >= 0) return false; // nested deeper
            leaf = rest;
            return true;
        }

        private async Task<bool> AnyObjectUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: 1, start: null, cancellationToken).ConfigureAwait(false);
            return page.Objects.Count > 0;
        }

        private async Task DeleteAllUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            string start = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                    await _session.Client.DeleteObjectAsync(obj.Name, cancellationToken).ConfigureAwait(false);
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));

            if (!string.IsNullOrEmpty(prefix))
            {
                try
                {
                    await _session.Client.DeleteObjectAsync(prefix, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // no marker to delete
                }
            }
        }

        private async Task CopyAllObjectsAsync(string sourcePrefix, IOciClient destinationClient, string destinationPrefix, CancellationToken cancellationToken)
        {
            string start = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(sourcePrefix, delimiter: null, limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    var destName = destinationPrefix + obj.Name.Substring(sourcePrefix.Length);
                    await _session.Client.CopyObjectAsync(
                        obj.Name,
                        destinationClient.Namespace,
                        destinationClient.Bucket,
                        destinationClient.Region,
                        destName,
                        cancellationToken).ConfigureAwait(false);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));

            // No explicit marker PUT: if the source had a marker it was copied
            // along with the other objects; if not, the destination stays
            // implicit (same invariant we keep on nested writes).
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
    }
}
