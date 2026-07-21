using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3
{
    public class AmazonS3Directory : FileDirectory, IRefreshable, ISignedUploadable
    {
        private const string DirectoryContentType = "application/x-directory";

        private readonly IS3Session _session;
        private readonly AmazonS3Directory _parent;
        private readonly string _prefix;
        private readonly string _rootPrefix;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;

        public override string Path => PathUtil.DisplayPath(_prefix);
        public override FileDirectory Parent => _parent;

        public override DateTime CreationTimeUtc => _creationTimeUtc;
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc;

        internal IS3Session SessionInternal => _session;
        internal string PrefixInternal => _prefix;
        internal string RootPrefixInternal => _rootPrefix;

        internal AmazonS3Directory(IS3Session session, string rootPrefix)
            : base(GetDisplayName(rootPrefix), rootPath: rootPrefix)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prefix = rootPrefix ?? string.Empty;
            _rootPrefix = _prefix;
            _parent = null;
        }

        internal AmazonS3Directory(AmazonS3Directory parent, string name)
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
                options: new S3WriteOptions { ContentType = DirectoryContentType },
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
            var key = PathUtil.ResolveSafeKey(_rootPrefix, _prefix, head);
            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(
                    key, empty, 0,
                    options: null,
                    cancellationToken).ConfigureAwait(false);
            }
            var file = new AmazonS3File(this, head, 0, DateTime.UtcNow);
            file.MarkLoaded();   // empty object just created; state is known.
            return file;
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

        public override FileEntry OpenFile(string name, bool createIfNotExists)
        {
            if (!createIfNotExists) return base.OpenFile(name, createIfNotExists);

            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                // Directory navigation may still create intermediate dirs
                // (cheap marker PUTs). Only the final file is a stub.
                var dir = OpenOrCreateChildDirectory(head, createIfNotExists: true);
                return dir.OpenFile(rest, createIfNotExists: true);
            }
            PathUtil.ValidateName(head);
            return new AmazonS3File(this, head);   // stub, IsLoaded = false
        }

        public override Task<FileEntry> OpenFileAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            if (!createIfNotExists) return base.OpenFileAsync(name, createIfNotExists, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name, createIfNotExists: true));
        }

        protected override FileDirectory OpenOrCreateChildDirectory(string segment, bool createIfNotExists)
        {
            if (createIfNotExists)
            {
                PathUtil.ValidateName(segment);
                return new AmazonS3Directory(this, segment);
            }
            return base.OpenOrCreateChildDirectory(segment, createIfNotExists);
        }

        protected override Task<FileDirectory> OpenOrCreateChildDirectoryAsync(string segment, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            // Zero-call branch is pure in-process construction — delegate to
            // the sync hook; strict (false) keeps the base async probe.
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

            var key = PathUtil.CombineKey(_prefix, name);
            try
            {
                var head = await _session.Client.HeadObjectAsync(key, cancellationToken).ConfigureAwait(false);
                var file = new AmazonS3File(this, name, head.ContentLength ?? 0, head.LastModified);
                // Populate the metadata snapshot from the same HEAD so the
                // caller doesn't need a second round-trip via Refresh().
                file.LoadMetadataFromHead(head);
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
        /// are pushed straight into S3's <c>StartAfter</c> parameter — a single
        /// round-trip gets you to the cursor regardless of how many objects
        /// precede it. This is the recommended way to paginate large
        /// listings.
        /// </para>
        /// <para>
        /// <b>Index offsets (<see cref="FileListOffset.FromIndex(int)"/>) are
        /// expensive on S3</b>: the protocol has no "skip N" primitive, so the
        /// driver walks every preceding object client-side until the index is
        /// reached. Cost grows linearly with the offset (API calls, bandwidth
        /// and latency), and on very large buckets this can be ruinous. Avoid
        /// index offsets for anything beyond small directories — prefer named
        /// offsets derived from the last item of the previous page.
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
            string continuationToken = null;
            // S3 StartAfter is an exclusive cursor. Build it from the current
            // prefix + leaf name so the server resumes listing after it,
            // avoiding a client-side skip-scan for named offsets.
            string startAfter = offset.IsNamed ? _prefix + offset.Name : null;
            int skipped = 0;
            int yielded = 0;
            do
            {
                var page = SyncBridge.Run(ct => _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: backendLimit, continuationToken: continuationToken, startAfter: startAfter, ct));
                foreach (var obj in page.Objects)
                {
                    if (!IsChildFile(obj.Key, out var leaf)) continue;
                    if (!regex.IsMatch(leaf)) continue;
                    if (!offset.IsNamed && skipped < offset.Index) { skipped++; continue; }
                    if (limit.HasValue && yielded >= limit.Value) yield break;
                    yielded++;
                    yield return new AmazonS3File(this, leaf, obj.Size ?? 0, obj.LastModified);
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Asynchronously lists files under this prefix, optionally paginated.
        /// </summary>
        /// <remarks>
        /// Same cost model as the sync <see cref="GetFiles"/>: named offsets
        /// ride on S3's <c>StartAfter</c> (cheap), index offsets require a
        /// client-side walk over every preceding object (expensive — avoid on
        /// large buckets).
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
            string continuationToken = null;
            string startAfter = offset.IsNamed ? _prefix + offset.Name : null;
            int skipped = 0;
            int yielded = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: backendLimit, continuationToken: continuationToken, startAfter: startAfter, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    if (!IsChildFile(obj.Key, out var leaf)) continue;
                    if (!regex.IsMatch(leaf)) continue;
                    if (!offset.IsNamed && skipped < offset.Index) { skipped++; continue; }
                    if (limit.HasValue && yielded >= limit.Value) yield break;
                    yielded++;
                    yield return new AmazonS3File(this, leaf, obj.Size ?? 0, obj.LastModified);
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));
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

        // === Directory resolution (whole path in one request) ===

        // Nullable handle for the internal callers (FileExists / Delete / ...).
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
                    new S3WriteOptions { ContentType = DirectoryContentType }, cancellationToken).ConfigureAwait(false);
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

        private string BuildNestedPrefix(string[] segments)
        {
            var result = _prefix ?? string.Empty;
            foreach (var seg in segments)
                result += seg + "/";
            return result;
        }

        private AmazonS3Directory BuildDirectoryChain(string[] segments)
        {
            AmazonS3Directory current = this;
            foreach (var seg in segments)
                current = new AmazonS3Directory(current, seg);
            return current;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
            string continuationToken = null;
            do
            {
                var page = SyncBridge.Run(ct => _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, continuationToken: continuationToken, startAfter: null, ct));
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = PathUtil.GetLeafName(childPrefix);
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new AmazonS3Directory(this, leaf);
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));
        }

#if NET8_0_OR_GREATER
        public override async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var regex = PathUtil.BuildSearchPatternRegex(searchPattern);
            string continuationToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, continuationToken: continuationToken, startAfter: null, cancellationToken).ConfigureAwait(false);
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = PathUtil.GetLeafName(childPrefix);
                    if (!regex.IsMatch(leaf)) continue;
                    yield return new AmazonS3Directory(this, leaf);
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));
        }
#endif

        public override bool FileExists(string name) => SyncBridge.Run(ct => FileExistsAsync(name, ct));

        public override async Task<bool> FileExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is AmazonS3Directory s3Dir)
                    return await s3Dir.FileExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }
            var key = PathUtil.CombineKey(_prefix, head);
            try
            {
                await _session.Client.HeadObjectAsync(key, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        public override bool DirectoryExists(string name) => SyncBridge.Run(ct => DirectoryExistsAsync(name, ct));

        // LIST(prefix, limit=1) covers both cases with a single request:
        // the explicit "/" marker (when present) or any implicit child key.
        // HEAD-first was cheaper only when markers were common; since the
        // driver no longer auto-creates markers on nested writes, LIST is
        // the only probe we need.
        public override async Task<bool> DirectoryExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is AmazonS3Directory s3Dir)
                    return await s3Dir.DirectoryExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }
            var childPrefix = PathUtil.CombinePrefix(_prefix, head);
            return await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false);
        }

        public override bool Exists(string name) => SyncBridge.Run(ct => ExistsAsync(name, ct));

        // File-or-directory in ONE request. A delimited LIST on the key groups
        // everything sharing the "<prefix>/name" prefix: the exact object (a
        // file) lands in Objects, while any deeper "<prefix>/name/..." collapses
        // into the "<prefix>/name/" common prefix (a directory). Siblings like
        // "name-x" or "namebaz" appear too but under different keys/prefixes, so
        // we match exactly and never false-positive. Beats HEAD-then-LIST, which
        // costs two round-trips on a billed backend.
        public override async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is AmazonS3Directory s3Dir)
                    return await s3Dir.ExistsAsync(rest, cancellationToken).ConfigureAwait(false);
                return false;
            }
            try { PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }

            var key = PathUtil.CombineKey(_prefix, head);
            var dirPrefix = key + "/";
            var page = await _session.Client.ListObjectsAsync(
                key, delimiter: "/", limit: null, continuationToken: null, startAfter: null, cancellationToken).ConfigureAwait(false);

            foreach (var obj in page.Objects)
                if (string.Equals(obj.Key, key, StringComparison.Ordinal))
                    return true; // exact object → a file
            foreach (var p in page.Prefixes)
                if (string.Equals(p, dirPrefix, StringComparison.Ordinal))
                    return true; // "<key>/" common prefix → a directory
            return false;
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
            var (head, rest) = SplitPath(name);
            if (rest != null)
            {
                var dir = await TryOpenDirectoryCoreAsync(head, cancellationToken).ConfigureAwait(false);
                if (dir is AmazonS3Directory s3Dir)
                {
                    await s3Dir.DeleteAsync(rest, cancellationToken).ConfigureAwait(false);
                    return;
                }
                throw new FileNotFoundException($"The item \"{name}\" was not found under \"{Path}\".");
            }
            PathUtil.ValidateName(head);

            var fileKey = PathUtil.CombineKey(_prefix, head);
            var dirPrefix = PathUtil.CombinePrefix(_prefix, head);

            // S3's DeleteObject is idempotent — it returns 204 whether or not
            // the key existed, so we can't infer "is this a file or a directory?"
            // from a DELETE alone. Probe with a single LIST(limit=1): if any
            // object lives under the dir-prefix, delete the tree; otherwise
            // issue an idempotent DELETE on the file key.
            if (await AnyObjectUnderPrefixAsync(dirPrefix, cancellationToken).ConfigureAwait(false))
            {
                await DeleteAllUnderPrefixAsync(dirPrefix, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Plain file delete — idempotent (no throw if missing) to match S3.
            await _session.Client.DeleteObjectAsync(fileKey, cancellationToken).ConfigureAwait(false);
        }

        // === ISignedUploadable ===

        public Uri GetSignedUploadUrl(string name, TimeSpan expiresIn, FileWriteOptions options = null)
            => SyncBridge.Run(ct => GetSignedUploadUrlAsync(name, expiresIn, options, ct));

        public async Task<Uri> GetSignedUploadUrlAsync(string name, TimeSpan expiresIn, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name cannot be null or empty.", nameof(name));
            if (expiresIn <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            // Resolve to a full key under this prefix. Accepts nested paths and
            // rejects sandbox escapes via the same SplitPath used by CreateFile.
            var key = ResolveLeafKey(name);
            var expiresUtc = DateTime.UtcNow.Add(expiresIn);
            var url = await _session.Client.GetPreSignedUploadUrlAsync(
                key,
                expiresUtc,
                AmazonS3File.NormalizeOptions(options),
                cancellationToken).ConfigureAwait(false);
            return new Uri(url);
        }
        
        private string ResolveLeafKey(string name)
        {
            // Walk the path through SplitPath to validate each segment (rejects
            // "..", absolute paths, etc) without actually opening any directories.
            var prefix = _prefix;
            var remaining = name;
            while (true)
            {
                var (head, rest) = SplitPath(remaining);
                if (rest == null)
                {
                    PathUtil.ValidateName(head);
                    return PathUtil.CombineKey(prefix, head);
                }
                PathUtil.ValidateName(head);
                prefix = PathUtil.CombinePrefix(prefix, head);
                remaining = rest;
            }
        }

        public override void DeleteIfExists(string name) => SyncBridge.Run(ct => DeleteIfExistsAsync(name, ct));

        /// <summary>
        /// Single-call delete on S3. <c>DeleteObject</c> is idempotent — returns
        /// <c>204</c> whether the object existed or not — so skipping the base
        /// implementation's <c>FileExists</c> + <c>DirectoryExists</c> probe
        /// saves up to one HEAD and one LIST per call. If <paramref name="name"/>
        /// resolves only as a directory, the LIST/DELETE cascade in
        /// <see cref="DeleteAsync(string, CancellationToken)"/> still runs.
        /// </summary>
        public override async Task DeleteIfExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            try
            {
                await DeleteAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // DeleteIfExists swallows the "nothing to delete" case.
            }
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

            // Rename never overwrites — a name already taken is an error.
            if (await _parent.ExistsAsync(newName, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException($"{_parent.Path}/{newName}");

            var destinationPrefix = PathUtil.CombinePrefix(_parent._prefix, newName);
            await CopyAllObjectsAsync(_prefix, _session.Client, destinationPrefix, cancellationToken).ConfigureAwait(false);
            await DeleteAllUnderPrefixAsync(_prefix, cancellationToken).ConfigureAwait(false);
            return new AmazonS3Directory(_parent, newName);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, ct));

        public override async Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var newDir = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
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

        public override FileDirectory CopyTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, ct));

        public override async Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory under the destination and
            // recurse with the single leaf.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                {
                    var deeper = await directory.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await CopyToAsync(deeper, leaf, cancellationToken).ConfigureAwait(false);
                }
                name = leaf;
            }

            if (directory is AmazonS3Directory s3Dir
                && S3SessionTarget.SameCredentials(s3Dir._session.Client, _session.Client))
            {
                var destinationPrefix = PathUtil.ResolveSafeChildPrefix(s3Dir._rootPrefix, s3Dir._prefix, name);
                await CopyAllObjectsAsync(_prefix, s3Dir._session.Client, destinationPrefix, cancellationToken).ConfigureAwait(false);
                return new AmazonS3Directory(s3Dir, name);
            }

            var newDir = await directory.CreateDirectoryAsync(name, cancellationToken).ConfigureAwait(false);
            CopyContentsGeneric(this, newDir);
            return newDir;
        }

        // === Helpers ===

        private bool IsChildFile(string key, out string leaf)
        {
            leaf = null;
            if (!key.StartsWith(_prefix, StringComparison.Ordinal)) return false;
            if (key.Length == _prefix.Length) return false; // own marker
            var rest = key.Substring(_prefix.Length);
            if (rest.EndsWith("/", StringComparison.Ordinal)) return false; // subdir marker
            if (rest.IndexOf('/') >= 0) return false; // nested deeper
            leaf = rest;
            return true;
        }

        private async Task<bool> AnyObjectUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: 1, continuationToken: null, startAfter: null, cancellationToken).ConfigureAwait(false);
            return page.Objects.Count > 0;
        }

        private async Task DeleteAllUnderPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            // Batch deletes in chunks of S3's per-request maximum (1000 keys
            // per DeleteObjects call). Each chunk is one round-trip instead
            // of N individual DELETE calls — for a 10k-object prefix this
            // collapses 10 000 requests into 10. Collect per-key errors S3
            // reports so callers see the full picture instead of aborting
            // on the first granular IAM rule or transient throttle.
            const int BatchSize = 1000;
            var failures = new List<Exception>();
            var pending = new List<string>(BatchSize);
            string continuationToken = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: null, continuationToken: continuationToken, startAfter: null, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    pending.Add(obj.Key);
                    if (pending.Count >= BatchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await FlushDeleteBatchAsync(pending, failures, cancellationToken).ConfigureAwait(false);
                    }
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));

            // Also include the prefix marker key in the trailing batch when
            // present. S3 treats it as just another object — same DeleteObjects
            // call deletes it. If the marker doesn't exist, S3 silently ignores
            // it (idempotent).
            if (!string.IsNullOrEmpty(prefix))
                pending.Add(prefix);

            if (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FlushDeleteBatchAsync(pending, failures, cancellationToken).ConfigureAwait(false);
            }

            if (failures.Count > 0)
                throw new AggregateException(
                    $"One or more objects under \"{prefix}\" could not be deleted ({failures.Count} failure(s)). The directory is partially deleted.",
                    failures);
        }

        private async Task FlushDeleteBatchAsync(List<string> keys, List<Exception> failures, CancellationToken cancellationToken)
        {
            try
            {
                var errors = await _session.Client.DeleteObjectsAsync(keys, cancellationToken).ConfigureAwait(false);
                foreach (var e in errors)
                    failures.Add(new FileHubException($"S3 DeleteObjects failed for key \"{e.Key}\" ({e.Code}): {e.Message}"));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                failures.Add(ex);
            }
            keys.Clear();
        }

        private async Task CopyAllObjectsAsync(string sourcePrefix, IS3Client destinationClient, string destinationPrefix, CancellationToken cancellationToken)
        {
            var sourceClient = _session.Client;
            string continuationToken = null;
            do
            {
                var page = await sourceClient.ListObjectsAsync(sourcePrefix, delimiter: null, limit: null, continuationToken: continuationToken, startAfter: null, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    var destKey = destinationPrefix + obj.Key.Substring(sourcePrefix.Length);
                    // Destination client issues the CopyObject so the request
                    // hits the destination's region endpoint — required for
                    // cross-region copies, harmless for same-region.
                    await destinationClient.CopyFromBucketAsync(
                        sourceClient.Bucket,
                        obj.Key,
                        destKey,
                        metadataReplace: false,
                        options: null,
                        cancellationToken).ConfigureAwait(false);
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));

            // No explicit marker PUT: if the source had a marker it was copied
            // along with the other objects; if not, destination stays implicit
            // (same invariant we keep on nested writes).
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
