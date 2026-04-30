using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3
{
    public class AmazonS3Directory : FileDirectory, IRefreshable
    {
        private const string DirectoryContentType = "application/x-directory";

        private readonly IS3Session _session;
        private readonly AmazonS3Directory _parent;
        private readonly string _prefix;
        private readonly string _rootPrefix;
        private readonly DirectoryPathMode _pathMode;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;

        public override string Path => S3PathUtil.DisplayPath(_prefix);
        public override FileDirectory Parent => _parent;

        public override DateTime CreationTimeUtc => _creationTimeUtc;
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc;

        internal IS3Session SessionInternal => _session;
        internal string PrefixInternal => _prefix;
        internal string RootPrefixInternal => _rootPrefix;

        internal AmazonS3Directory(IS3Session session, string rootPrefix)
            : this(session, rootPrefix, DirectoryPathMode.Direct) { }

        internal AmazonS3Directory(IS3Session session, string rootPrefix, DirectoryPathMode pathMode)
            : base(GetDisplayName(rootPrefix), rootPath: rootPrefix)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prefix = rootPrefix ?? string.Empty;
            _rootPrefix = _prefix;
            _parent = null;
            _pathMode = pathMode;
        }

        internal AmazonS3Directory(AmazonS3Directory parent, string name)
            : base(name, rootPath: parent?.RootPrefixInternal)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _session = parent._session;
            _rootPrefix = parent._rootPrefix;
            _prefix = S3PathUtil.CombinePrefix(parent._prefix, name);
            _pathMode = parent._pathMode;
        }

        private static string GetDisplayName(string rootPrefix)
        {
            if (string.IsNullOrEmpty(rootPrefix))
                return "/";
            return S3PathUtil.GetLeafName(rootPrefix);
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
                _prefix, empty, contentLength: 0, contentType: DirectoryContentType,
                userMetadata: null, storageClass: null, serverSideEncryption: null,
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
            var key = S3PathUtil.ResolveSafeObjectKey(_rootPrefix, _prefix, head);
            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(
                    key, empty, 0,
                    contentType: null,
                    userMetadata: null,
                    storageClass: null,
                    serverSideEncryption: null,
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

        // Strict (createIfNotExists == false): fall through to base, which
        // calls TryOpenFile → 1 × HEAD → loaded file or FileNotFoundException.
        //
        // createIfNotExists == true: RETURN STUB. No HEAD, no PutObject.
        // S3 is pay-per-request; deferring creation to the first write saves
        // round-trips. Caller's responsibility: write to materialize, read
        // fails with FileNotFoundException if the object doesn't exist.

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
            S3PathUtil.ValidateName(head);
            return new AmazonS3File(this, head);   // stub, IsLoaded = false
        }

        public override Task<FileEntry> OpenFileAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            if (!createIfNotExists) return base.OpenFileAsync(name, createIfNotExists, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name, createIfNotExists: true));
        }

        // S3 "directories" are only key prefixes — there is no real container
        // entity. When the caller signals `createIfNotExists: true` we don't
        // need to HEAD the marker, LIST children, nor PUT an empty marker:
        // the prefix is implicitly usable the moment a child key is written.
        // Strict (false) keeps the base semantics so missing paths still
        // throw DirectoryNotFoundException.
        protected override FileDirectory OpenOrCreateChildDirectory(string segment, bool createIfNotExists)
        {
            if (createIfNotExists)
            {
                S3PathUtil.ValidateName(segment);
                return new AmazonS3Directory(this, segment);
            }
            return base.OpenOrCreateChildDirectory(segment, createIfNotExists);
        }

        private async Task<FileEntry> TryOpenFileCoreAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                S3PathUtil.ValidateName(name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var key = S3PathUtil.CombineObjectKey(_prefix, name);
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
            var regex = S3PathUtil.BuildSearchPatternRegex(searchPattern);
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
            var regex = S3PathUtil.BuildSearchPatternRegex(searchPattern);
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

            var leaf = head ?? name;
            var childPrefix = S3PathUtil.ResolveSafeChildPrefix(_rootPrefix, _prefix, leaf);

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(childPrefix, empty, 0, DirectoryContentType, null, null, null, cancellationToken).ConfigureAwait(false);
            }
            return new AmazonS3Directory(this, leaf);
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
                if (childResult is AmazonS3Directory s3Child)
                    return await s3Child.TryOpenDirectoryCoreAsync(rest, cancellationToken).ConfigureAwait(false);
                return null;
            }

            var leaf = head ?? name;
            try
            {
                S3PathUtil.ValidateName(leaf);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var childPrefix = S3PathUtil.CombinePrefix(_prefix, leaf);
            if (await AnyObjectUnderPrefixAsync(childPrefix, cancellationToken).ConfigureAwait(false))
                return new AmazonS3Directory(this, leaf);
            return null;
        }

        private async Task<FileDirectory> CreateDirectoryDirectAsync(string nestedName, CancellationToken cancellationToken)
        {
            var segments = ValidateAndSplitNestedSegments(nestedName);
            var fullPrefix = BuildNestedPrefix(segments);
            S3PathUtil.EnsureWithinRootPrefix(_rootPrefix, fullPrefix);

            using (var empty = new MemoryStream())
            {
                await _session.Client.PutObjectAsync(fullPrefix, empty, 0, DirectoryContentType, null, null, null, cancellationToken).ConfigureAwait(false);
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
            S3PathUtil.EnsureWithinRootPrefix(_rootPrefix, fullPrefix);

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
                if (seg == "." || seg == "..")
                    throw new FileHubException($"Path \"{nestedName}\" contains invalid segment \"{seg}\".");
                S3PathUtil.ValidateName(seg);
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

        private AmazonS3Directory BuildDirectoryChain(string[] segments)
        {
            AmazonS3Directory current = this;
            foreach (var seg in segments)
                current = new AmazonS3Directory(current, seg);
            return current;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var regex = S3PathUtil.BuildSearchPatternRegex(searchPattern);
            string continuationToken = null;
            do
            {
                var page = SyncBridge.Run(ct => _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, continuationToken: continuationToken, startAfter: null, ct));
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = S3PathUtil.GetLeafName(childPrefix);
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
            var regex = S3PathUtil.BuildSearchPatternRegex(searchPattern);
            string continuationToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _session.Client.ListObjectsAsync(_prefix, delimiter: "/", limit: null, continuationToken: continuationToken, startAfter: null, cancellationToken).ConfigureAwait(false);
                foreach (var childPrefix in page.Prefixes)
                {
                    var leaf = S3PathUtil.GetLeafName(childPrefix);
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
            try { S3PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }
            var key = S3PathUtil.CombineObjectKey(_prefix, head);
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
            try { S3PathUtil.ValidateName(head); } catch (ArgumentException) { return false; }
            var childPrefix = S3PathUtil.CombinePrefix(_prefix, head);
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
            S3PathUtil.ValidateName(head);

            var key = S3PathUtil.CombineObjectKey(_prefix, head);
            try
            {
                await _session.Client.DeleteObjectAsync(key, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (FileNotFoundException)
            {
                // fall through to directory delete attempt
            }

            var childPrefix = S3PathUtil.CombinePrefix(_prefix, head);
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

            S3PathUtil.ValidateName(newName);
            var destinationPrefix = S3PathUtil.CombinePrefix(_parent._prefix, newName);
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
            if (directory is AmazonS3Directory s3Dir
                && S3SessionTarget.SameCredentials(s3Dir._session.Client, _session.Client))
            {
                var destinationPrefix = S3PathUtil.ResolveSafeChildPrefix(s3Dir._rootPrefix, s3Dir._prefix, name);
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
            // Collect per-object failures instead of aborting on the first
            // one. A single 403 from a granular IAM rule, or a transient
            // throttle, would otherwise leave the rest of the directory
            // intact and force the caller to retry from a half-deleted state.
            List<Exception> failures = null;
            string continuationToken = null;
            do
            {
                var page = await _session.Client.ListObjectsAsync(prefix, delimiter: null, limit: null, continuationToken: continuationToken, startAfter: null, cancellationToken).ConfigureAwait(false);
                foreach (var obj in page.Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await _session.Client.DeleteObjectAsync(obj.Key, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        (failures ??= new List<Exception>()).Add(ex);
                    }
                }
                continuationToken = page.NextContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));

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
                        contentType: null,
                        userMetadata: null,
                        storageClass: null,
                        serverSideEncryption: null,
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
