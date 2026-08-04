using System;
using System.Collections.Generic;
using System.IO;
#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage
{
    public class OracleObjectStorageDirectory : FileDirectory, IRefreshable, ISignedUploadable
    {
        private const string DirectoryContentType = "application/x-directory";

        private readonly IOciSession _session;
        private readonly OracleObjectStorageDirectory _parent;
        private readonly string _prefix;
        private readonly string _rootPrefix;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;

        public override string Path => PathUtil.DisplayPath(_prefix);
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
        /// <summary>Constructor used for the root directory of a FileHub.</summary>
        internal OracleObjectStorageDirectory(IOciSession session, string rootPrefix)
            : base(GetDisplayName(rootPrefix), rootPath: rootPrefix)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prefix = rootPrefix ?? string.Empty;
            _rootPrefix = _prefix;
            _parent = null;
        }

        /// <summary>Constructor used for child directories.</summary>
        internal OracleObjectStorageDirectory(OracleObjectStorageDirectory parent, string name)
            : base(name, rootPath: parent?.RootPrefixInternal)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _session = parent._session;
            _rootPrefix = parent._rootPrefix;
            _prefix = PathUtil.CombinePrefix(parent._prefix, name);
        }

        private static string GetDisplayName(string rootPrefix)
        {
            if (string.IsNullOrEmpty(rootPrefix))
                return "/";
            return PathUtil.GetLeafName(rootPrefix);
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
                _prefix, empty, contentLength: 0,
                options: new OciWriteOptions { ContentType = DirectoryContentType },
                cancellationToken).ConfigureAwait(false);
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
            
            if (await ExistsAsync(head, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(CombineChildPath(head));

            var objectName = PathUtil.ResolveSafeKey(_rootPrefix, _prefix, head);
            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(objectName, empty, 0, options: null, cancellationToken).ConfigureAwait(false);
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
            PathUtil.ValidateName(head);
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
                PathUtil.ValidateName(segment);
                return new OracleObjectStorageDirectory(this, segment);
            }
            return base.OpenOrCreateChildDirectory(segment, createIfNotExists);
        }

        protected override Task<FileDirectory> OpenOrCreateChildDirectoryAsync(string segment, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            if (createIfNotExists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(OpenOrCreateChildDirectory(segment, createIfNotExists: true));
            }
            return base.OpenOrCreateChildDirectoryAsync(segment, createIfNotExists, cancellationToken);
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

            var objectName = PathUtil.CombineKey(_prefix, name);
            try
            {
                var head = await _session.Client.HeadObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                var file = new OracleObjectStorageFile(this, name);
                file.LoadFromHead(head);
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
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
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
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
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

        // === Directory leaf primitives (base drives split/branch/recurse) ===

        // Nullable handle for the internal callers.
        private async Task<FileDirectory> TryOpenDirectoryCoreAsync(string name, CancellationToken cancellationToken = default)
            => (await TryOpenDirectoryAsync(name, cancellationToken).ConfigureAwait(false)).Directory;

        // One PUT creates the whole path's marker.
        public override async Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var segments = PathUtil.SplitAndValidateSegments(name);
            var fullPrefix = BuildNestedPrefix(segments);
            PathUtil.EnsureWithinRootPrefix(_rootPrefix, fullPrefix);

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(fullPrefix, empty, 0,
                    new OciWriteOptions { ContentType = DirectoryContentType }, cancellationToken).ConfigureAwait(false);
            }
            return BuildDirectoryChain(segments);
        }

        // One LIST proves the whole path exists.
        public override async Task<(FileDirectory Directory, bool Exists)> TryOpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            string[] segments;
            try { segments = PathUtil.SplitAndValidateSegments(name); }
            catch (ArgumentException) { return (null, false); }

            var fullPrefix = BuildNestedPrefix(segments);
            PathUtil.EnsureWithinRootPrefix(_rootPrefix, fullPrefix);

            return await AnyObjectUnderPrefixAsync(fullPrefix, cancellationToken).ConfigureAwait(false)
                ? (BuildDirectoryChain(segments), true)
                : (null, false);
        }

        // === ISignedUploadable ===

        public Uri GetSignedUploadUrl(string name, TimeSpan expiresIn, FileWriteOptions options = null)
            => SyncBridge.Run(ct => GetSignedUploadUrlAsync(name, expiresIn, options, ct));

        public async Task<Uri> GetSignedUploadUrlAsync(string name, TimeSpan expiresIn, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be null or empty.", nameof(name));
            if (expiresIn <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            // OCI PARs can't bind request headers to the URL. Rather than hand
            // back a URL the caller believes is header-constrained but isn't
            // (S3 binds them), refuse header-binding options outright.
            if (options != null && (options.ContentType != null || options.CacheControl != null || options.Metadata != null))
                throw new NotSupportedException(
                    "OCI pre-authenticated requests cannot bind Content-Type, Cache-Control or metadata headers to " +
                    "the upload URL. Omit these options, or enforce the headers server-side after the upload completes.");

            // Resolve to a full object name under this prefix. Reuses the
            // nested-path validation PathUtil already applies — rejects
            // ".." / absolute paths / invalid segments.
            var segments = PathUtil.SplitAndValidateSegments(name);
            if (segments.Length == 0) throw new ArgumentException("Name cannot be empty.", nameof(name));

            var prefix = _prefix ?? string.Empty;
            for (int i = 0; i < segments.Length - 1; i++)
                prefix += segments[i] + "/";
            var leafName = segments[segments.Length - 1];
            var objectName = PathUtil.CombineKey(prefix, leafName);

            var client = _session.Client;
            var parName = $"filehub-upload-{Guid.NewGuid():N}";
            var timeExpires = DateTime.UtcNow.Add(expiresIn);

            var accessUri = await client.CreatePreauthenticatedWriteRequestAsync(objectName, parName, timeExpires, cancellationToken).ConfigureAwait(false);
            return new Uri($"https://objectstorage.{client.Region}.oraclecloud.com{accessUri}");
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
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                var page = SyncBridge.Run(ct => _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start, ct));
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = PathUtil.GetLeafName(childPrefix);
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
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
            string start = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = PathUtil.GetLeafName(childPrefix);
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
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is OracleObjectStorageDirectory ociDir)
                    return await ociDir.FileExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }
            var objectName = PathUtil.CombineKey(_prefix, head);
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
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is OracleObjectStorageDirectory ociDir)
                    return await ociDir.DirectoryExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }
            var childPrefix = PathUtil.CombinePrefix(_prefix, head);
            return await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
        }

        public override bool Exists(string name) => SyncBridge.Run(ct => ExistsAsync(name, ct));

        // File-or-directory in ONE request. A delimited LIST on the object name
        // groups everything sharing the "<prefix>/name" prefix: the exact object
        // (a file) lands in Objects, while any deeper "<prefix>/name/..." collapses
        // into the "<prefix>/name/" common prefix (a directory). Siblings such as
        // "name-x" sit under other keys/prefixes, so exact matching never
        // false-positives. Saves the HEAD-then-LIST double round-trip.
        public override async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is OracleObjectStorageDirectory ociDir)
                    return await ociDir.ExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }

            var objectName = PathUtil.CombineKey(_prefix, head);
            var dirPrefix = objectName + "/";
            var page = await _session.Client.ListObjectsAsync(
                objectName, delimiter: "/", limit: null, start: null, cancellationToken).ConfigureAwait(false);

            foreach (var obj in page.Objects)
                if (string.Equals(obj.Name, objectName, StringComparison.Ordinal))
                    return true; // exact object → a file
            foreach (var p in page.Prefixes)
                if (string.Equals(p, dirPrefix, StringComparison.Ordinal))
                    return true; // "<objectName>/" common prefix → a directory
            return false;
        }

        public override void Delete(bool recursive = false) => SyncBridge.Run(ct => DeleteAsync(recursive, ct));

        public override async Task DeleteAsync(bool recursive = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_prefix == _rootPrefix)
                throw new NotSupportedException("Cannot delete the root directory of the FileHub.");

            if (!recursive && await AnyChildUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false))
                throw new DirectoryNotEmptyException(Path);

            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
        }

        public override void Delete(string name, bool recursive = false) => SyncBridge.Run(ct => DeleteAsync(name, recursive, ct));

        public override async Task DeleteAsync(string name, bool recursive = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is OracleObjectStorageDirectory ociDir)
                    await ociDir.DeleteAsync(rest, recursive, cancellationToken).ConfigureAwait(false);
                return;
            }
            PathUtil.ValidateName(head);

            var objectName = PathUtil.CombineKey(_prefix, head);
            try
            {
                await _session.Client.DeleteObjectAsync(objectName, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (FileNotFoundException)
            {
                // fall through to directory delete attempt
            }

            var childPrefix = PathUtil.CombinePrefix(_prefix, head);
            if (await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
            {
                if (!recursive && await AnyChildUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
                    throw new DirectoryNotEmptyException(CombineChildPath(head));

                await DeleteAllUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
            }
        }

        public override FileDirectory Rename(string newName) => SyncBridge.Run(ct => RenameAsync(newName, ct));

        public override async Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (_parent == null)
                throw new NotSupportedException("Cannot rename the root directory.");
            NestedPath.EnsureLeaf(newName);

            PathUtil.ValidateName(newName);

            // Rename never overwrites — a name already taken is an error.
            if (await _parent.ExistsAsync(newName, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(_parent.Path, newName));

            var destinationPrefix = PathUtil.CombinePrefix(_parent._prefix, newName);
            await CopyAllObjectsAsync(_prefix, _session.Client, destinationPrefix, cancellationToken).ConfigureAwait(false);
            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            return new OracleObjectStorageDirectory(_parent, newName);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name, bool overwrite = false)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, overwrite, ct));

        public override async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var newDir = await CopyToAsync(directory, name, overwrite, cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
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
            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and recurse with the leaf.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                {
                    var deeper = await directory.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await CopyToAsync(deeper, leaf, overwrite, cancellationToken).ConfigureAwait(false);
                }
                name = leaf;
            }

            if (directory is OracleObjectStorageDirectory ociDir
                && OciSessionTarget.SameCredentials(ociDir._session.Client, _session.Client))
            {
                var destinationPrefix = PathUtil.ResolveSafeChildPrefix(ociDir._rootPrefix, ociDir._prefix, name);
                // Same namespace + region + bucket + prefix means copying/moving
                // the directory onto itself — refuse before recopying every
                // object. Region is part of identity: a bucket name can repeat
                // across regions within a namespace.
                if (IsSameBucket(ociDir))
                {
                    // Same prefix = onto itself; a prefix under the source = into a
                    // descendant, which would recurse (copied objects reappear
                    // under the source prefix).
                    if (string.Equals(destinationPrefix, _prefix, StringComparison.Ordinal))
                        throw new FileAlreadyExistsException($"Cannot copy directory \"{Path}\" onto itself.", Path);
                    if (IsDescendantPath(destinationPrefix))
                        throw new FileHubException($"Cannot copy directory \"{Path}\" into one of its descendants.");
                }
                // overwrite: false must not clobber an existing destination. The
                // server-side CopyObject loop below always overwrites colliding
                // keys, so guard with a LIST up-front.
                if (!overwrite && await ociDir.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                    throw new FileAlreadyExistsException(PathUtil.JoinDisplay(ociDir.Path, name));
                await CopyAllObjectsAsync(_prefix, ociDir._session.Client, destinationPrefix, cancellationToken).ConfigureAwait(false);
                return new OracleObjectStorageDirectory(ociDir, name);
            }

            if (!overwrite && await directory.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(directory.Path, name));

            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
            CopyContentsGeneric(this, newDir, overwrite);
            return newDir;
        }

        // === Helpers ===

        // True when the other directory resolves to the same physical bucket.
        // Region is part of identity: a bucket name can repeat across regions
        // within a namespace, so namespace + region + bucket must all match.
        private bool IsSameBucket(OracleObjectStorageDirectory other)
            => string.Equals(other._session.Client.Namespace, _session.Client.Namespace, StringComparison.Ordinal)
               && string.Equals(other._session.Client.Region, _session.Client.Region, StringComparison.Ordinal)
               && string.Equals(other._session.Client.Bucket, _session.Client.Bucket, StringComparison.Ordinal);

        // True when destinationPrefix lives strictly beneath this directory's own
        // prefix — used to reject move/copy of a directory into its own subtree.
        private bool IsDescendantPath(string destinationPrefix)
            => !string.IsNullOrEmpty(_prefix)
               && !string.Equals(destinationPrefix, _prefix, StringComparison.Ordinal)
               && destinationPrefix.StartsWith(_prefix, StringComparison.Ordinal);

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

        // "Empty directory" test: any object under the prefix other than the
        // directory's own marker key (whose name equals the prefix). LIST 2 so a
        // page returning only the marker still lets us see whether a child
        // follows it.
        private async Task<bool> AnyChildUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: 2, start: null, cancellationToken).ConfigureAwait(false);
            foreach (var obj in page.Objects)
                if (!string.Equals(obj.Name, prefix, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private async Task DeleteAllUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            // Collect per-object failures instead of aborting on the first
            // one. A single 403 from a granular IAM rule, or a transient
            // throttle, would otherwise leave the rest of the directory
            // intact and force the caller to retry from a half-deleted state.
            List<Exception> failures = null;
            string start = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: null, start: start, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await _session.Client.DeleteObjectAsync(obj.Name, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        (failures ??= new List<Exception>()).Add(ex);
                    }
                }
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
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    (failures ??= new List<Exception>()).Add(ex);
                }
            }

            if (failures != null)
                throw new AggregateException(
                    $"One or more objects under \"{prefix}\" could not be deleted ({failures.Count} failure(s)). The directory is partially deleted.",
                    failures);
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
                    var handle = await _session.Client.CopyObjectAsync(
                        obj.Name,
                        destinationClient.Namespace,
                        destinationClient.Bucket,
                        destinationClient.Region,
                        destName,
                        cancellationToken).ConfigureAwait(false);
                    // OCI CopyObject is an async work request. Wait for it to
                    // reach a terminal state before moving on — otherwise the
                    // directory copy (and any move built on it) could return
                    // before the objects are durably copied server-side.
                    await handle.WaitAndRequestCancellationAsync(progress: null, cancellationToken).ConfigureAwait(false);
                }
                start = page.NextStartWith;
            } while (!string.IsNullOrEmpty(start));

            // No explicit marker PUT: if the source had a marker it was copied
            // along with the other objects; if not, the destination stays
            // implicit (same invariant we keep on nested writes).
        }

        private static void CopyContentsGeneric(FileDirectory source, FileDirectory destination, bool overwrite)
        {
            foreach (var file in source.GetFiles())
                file.CopyTo(destination, file.Name, progress: null, overwrite: overwrite);

            foreach (var subDir in source.GetDirectories())
            {
                var newSubDir = destination.CreateDirectory(subDir.Name);
                CopyContentsGeneric(subDir, newSubDir, overwrite);
            }
        }
    }
}
