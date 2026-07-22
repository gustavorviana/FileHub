using FileHub.OracleObjectStorage.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage
{
    public class OracleObjectStorageFile : FileEntry, IUrlAccessible, IRefreshable, ILazyLoad
    {
        internal const string ChangedAtTag = "_changedAt";

        /// <summary>
        /// Portable minimum multipart capability reported by FileHub. OCI does
        /// not publish an enforced minimum; actual buffering uses the configured
        /// <see cref="MultipartStreamOptions.PartSize"/>.
        /// </summary>
        internal const long OciMinimumPartSize = 5L * 1024 * 1024;

        private readonly OracleObjectStorageDirectory _parent;
        private long _length;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;
        private FileMetadata _metadata = new FileMetadata();
        private bool _isLoaded;
        private OciFileStreamBase _lastOpenStream;

        internal FileMetadata CachedMetadata => _metadata;

        /// <summary>
        /// <c>true</c> once the file's state has been loaded from OCI.
        /// <c>false</c> on pending stubs from
        /// <c>OpenFile(name, createIfNotExists: true)</c>.
        /// </summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>Driver-internal: flip <see cref="IsLoaded"/> to <c>true</c> without HEAD.</summary>
        internal void MarkLoaded() => _isLoaded = true;

        public override FileDirectory Parent => _parent;
        public override string Path => ConcatPath(_parent.Path, Name);

        /// <summary>
        /// Cached content length. Returns the last known value — call
        /// <see cref="Refresh"/> / <see cref="RefreshAsync"/> to re-sync with
        /// the bucket. Writes through this driver update the cached length as
        /// data is streamed, so the common write-then-read flow works without
        /// an explicit refresh.
        /// </summary>
        public override long Length => _length;

        /// <summary>Cached creation timestamp. See <see cref="Length"/> for refresh semantics.</summary>
        public override DateTime CreationTimeUtc => _creationTimeUtc;

        /// <summary>
        /// Cached last-write timestamp, derived from the object's
        /// <see cref="ChangedAtTag"/> metadata at load time, falling back to
        /// <see cref="CreationTimeUtc"/>. Drivers do not do hidden I/O in getters.
        /// </summary>
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc == default ? _creationTimeUtc : _lastWriteTimeUtc;

        internal string ObjectName => PathUtil.CombineKey(_parent.PrefixInternal, Name);
        internal IOciSession SessionInternal => _parent.SessionInternal;
        internal long LengthInternal { get => _length; set => _length = value; }

        internal OracleObjectStorageFile(OracleObjectStorageDirectory parent, string name) : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = -1;
        }

        internal OracleObjectStorageFile(OracleObjectStorageDirectory parent, string name, long length, DateTime? createdUtc)
            : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = length;
            _creationTimeUtc = createdUtc ?? default;
        }

        // === IRefreshable ===

        public void Refresh() => SyncBridge.Run(ct => RefreshAsync(ct));

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = await SessionInternal.Client.HeadObjectAsync(ObjectName, cancellationToken).ConfigureAwait(false);
            LoadFromHead(head);
        }

        /// <summary>
        /// Driver-internal: populate the cached snapshot (length, timestamp,
        /// content type, user metadata) from a HEAD result and flip
        /// <see cref="IsLoaded"/> to <c>true</c> — without paying a second
        /// HEAD. Used by the strict open path that already issued one.
        /// </summary>
        internal void LoadFromHead(OciHeadResult head)
        {
            _length = head.ContentLength ?? -1;
            _creationTimeUtc = head.LastModified ?? default;
            _lastWriteTimeUtc = ReadChangedAt(head.OpcMeta) ?? _creationTimeUtc;
            _metadata = BuildSnapshot(head.ContentType, head.CacheControl, head.OpcMeta);
            _isLoaded = true;
        }

        // Build the user-facing snapshot, stripping the driver-internal
        // last-write bookkeeping tag so it never surfaces to consumers.
        private static FileMetadata BuildSnapshot(string contentType, string cacheControl, Dictionary<string, string> opcMeta)
        {
            if (opcMeta == null)
                return new FileMetadata(contentType: contentType, cacheControl: cacheControl);
            var userTags = new Dictionary<string, string>(opcMeta, StringComparer.OrdinalIgnoreCase);
            userTags.Remove(ChangedAtTag);
            return new FileMetadata(contentType: contentType, cacheControl: cacheControl, tags: userTags);
        }

        private static DateTime? ReadChangedAt(Dictionary<string, string> opcMeta)
        {
            if (opcMeta != null && opcMeta.TryGetValue(ChangedAtTag, out var value)
                && DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed;
            return null;
        }

        // === Exists (sync delegates to async) ===

        public override bool Exists() => SyncBridge.Run(ct => ExistsAsync(ct));

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return _length >= 0;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        // === Streams ===

        public override Stream GetReadStream() => OpenReadStream();

        public override Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenReadStream());
        }

        private OciReadStream OpenReadStream()
        {
            ThrowIfStreamOpen();
            return Track(new OciReadStream(this));
        }

        private OciObjectStream OpenWriteStream(FileWriteOptions options)
        {
            ThrowIfStreamOpen();

            var ociWriteOptions = NormalizeOptions(options);
            var multipart = ociWriteOptions?.Multipart ?? SessionInternal.Multipart;
            OracleObjectStorageFileHub.ValidateMultipartOptions(multipart, nameof(options));

            return Track(new OciObjectStream(
                this,
                ociWriteOptions,
                multipart));
        }

        internal static OciWriteOptions NormalizeOptions(FileWriteOptions options)
        {
            if (options == null) return null;
            if (options is OciWriteOptions s3) return s3;

            return new OciWriteOptions
            {
                ContentType = options.ContentType,
                CacheControl = options.CacheControl,
                Metadata = options.Metadata,
                StreamPreference = options.StreamPreference,
            };
        }

        /// <summary>
        /// Registers a freshly created stream in the "one stream open per
        /// file" latch. The latch clears when the stream raises Disposed.
        /// </summary>
        private T Track<T>(T stream) where T : OciFileStreamBase
        {
            _lastOpenStream = stream;
            stream.Disposed += OnStreamDisposed;
            return stream;
        }

        private void ThrowIfStreamOpen()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(OracleObjectStorageFile));
            if (_lastOpenStream != null)
                throw new InvalidOperationException("A stream is already open for this file. Dispose it before opening another.");
        }

        private void OnStreamDisposed(object sender, EventArgs e)
        {
            if (_lastOpenStream != null)
                _lastOpenStream.Disposed -= OnStreamDisposed;
            _lastOpenStream = null;
        }

        /// <summary>
        /// Called by <see cref="OciObjectStream"/> at the end of a committed
        /// upload so the file reflects the new length and modification time
        /// without forcing a server round-trip. The timestamp is set
        /// client-side via the <see cref="ChangedAtTag"/> metadata; callers
        /// that need the authoritative server timestamp should call
        /// <see cref="Refresh"/>.
        /// </summary>
        internal void OnWriteCommitted(long bytesWritten, DateTime lastWriteUtc, FileMetadata snapshot)
        {
            _length = bytesWritten;
            _lastWriteTimeUtc = lastWriteUtc;
            _metadata = snapshot;
            _isLoaded = true;
        }

        // === Mutations (sync delegates to async) ===

        public override void Delete() => SyncBridge.Run(ct => DeleteAsync(ct));

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await SessionInternal.Client.DeleteObjectAsync(ObjectName, cancellationToken).ConfigureAwait(false);
            _length = -1;
        }

        public override FileEntry Rename(string newName) => SyncBridge.Run(ct => RenameAsync(newName, ct));

        public override async Task<FileEntry> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and move into it.
            if (NestedPath.HasSeparator(newName))
            {
                if (NestedPath.TrySplitLeaf(newName, out var subPath, out var leaf))
                {
                    var targetDir = await _parent.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await MoveToAsync(targetDir, leaf, progress: null, overwrite: false, cancellationToken).ConfigureAwait(false);
                }
                newName = leaf;
            }

            PathUtil.ValidateName(newName);

            // Rename never overwrites — the server-side rename would replace an
            // existing object silently, so guard with a HEAD. Best-effort.
            if (await _parent.ExistsAsync(newName, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException($"{_parent.Path}/{newName}");

            var destinationObject = PathUtil.CombineKey(_parent.PrefixInternal, newName);

            await SessionInternal.Client.RenameObjectAsync(ObjectName, destinationObject, cancellationToken).ConfigureAwait(false);

            Name = newName;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, progress, overwrite, ct));

        public override async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and recurse with the leaf.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                {
                    var deeper = await directory.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await MoveToAsync(deeper, leaf, progress, overwrite, cancellationToken).ConfigureAwait(false);
                }
                name = leaf;
            }

            if (directory is OracleObjectStorageDirectory ociDir
                && OciSessionTarget.SameCredentials(ociDir.SessionInternal.Client, SessionInternal.Client)
                && string.Equals(ociDir.SessionInternal.Client.Namespace, SessionInternal.Client.Namespace, StringComparison.Ordinal)
                && string.Equals(ociDir.SessionInternal.Client.Bucket, SessionInternal.Client.Bucket, StringComparison.Ordinal))
            {
                PathUtil.ValidateName(name);
                // overwrite: false must not clobber an existing object — the
                // server-side rename would replace it silently. Best-effort HEAD.
                if (!overwrite && await ociDir.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                    throw new FileAlreadyExistsException($"{ociDir.Path}/{name}");
                // Same rationale as CopyToAsync — load the source so the new
                // file doesn't carry _length = -1.
                if (!_isLoaded)
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                var destinationObject = PathUtil.CombineKey(ociDir.PrefixInternal, name);
                await SessionInternal.Client.RenameObjectAsync(ObjectName, destinationObject, cancellationToken).ConfigureAwait(false);
                progress?.Report(new TransferStatus(_length, _length));
                return new OracleObjectStorageFile(ociDir, name, _length, _creationTimeUtc);
            }

            var newFile = await CopyToAsync(directory, name, progress, overwrite, cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // Source gone already — move is effectively complete.
            }
            catch (Exception ex)
            {
                throw new PartialMoveException(
                    $"File was copied to \"{newFile.Path}\" but the original at \"{Path}\" could not be deleted. " +
                    $"The move is partial — remove the source manually.",
                    sourcePath: Path,
                    destinationPath: newFile.Path,
                    innerException: ex);
            }
            return newFile;
        }

        public override FileEntry CopyTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, progress, overwrite, ct));

        public override async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true, CancellationToken cancellationToken = default)
        {
            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and recurse with the leaf.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                {
                    var deeper = await directory.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await CopyToAsync(deeper, leaf, progress, overwrite, cancellationToken).ConfigureAwait(false);
                }
                name = leaf;
            }

            if (directory is OracleObjectStorageDirectory ociDir
                && OciSessionTarget.SameCredentials(ociDir.SessionInternal.Client, SessionInternal.Client))
            {
                PathUtil.ValidateName(name);

                if (!overwrite && await ociDir.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                    throw new FileAlreadyExistsException($"{ociDir.Path}/{name}");

                // Ensure we know the source size — propagating _length = -1
                // from an unrefreshed stub would make the new file look
                // missing to any consumer that reads Length.
                if (!_isLoaded && progress != null)
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);

                var destinationObject = PathUtil.CombineKey(ociDir.PrefixInternal, name);
                var destClient = ociDir.SessionInternal.Client;
                var operationHandle = await SessionInternal.Client.CopyObjectAsync(
                    ObjectName,
                    destClient.Namespace,
                    destClient.Bucket,
                    destClient.Region,
                    destinationObject,
                    cancellationToken).ConfigureAwait(false);


                var progressReporter = TransferStatusProgress.FromCallback(_length, progress);

                await operationHandle.WaitAndRequestCancellationAsync(progressReporter, cancellationToken);

                progressReporter?.Report(100);

                return new OracleObjectStorageFile(ociDir, name, _length, _creationTimeUtc);
            }
            return await base.CopyToAsync(directory, name, progress, overwrite, cancellationToken).ConfigureAwait(false);
        }

        // === FileWriteOptions / metadata-via-options surface ===

        /// <summary>
        /// Open a write stream whose commit applies <paramref name="options"/>
        /// on <c>PutObject</c>. Options live with the stream — no cross-call
        /// staging on the file. <see cref="FileWriteOptions.StreamPreference"/>
        /// selects the commit strategy: <see cref="WriteStreamPreference.Multipart"/>
        /// skips the buffering phase and opens the multipart upload on the
        /// first written byte; <see cref="WriteStreamPreference.Single"/>
        /// never spills — the whole payload buffers in memory and commits as
        /// one <c>PutObject</c>.
        /// </summary>
        public override Task<Stream> GetWriteStreamAsync(FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenWriteStream(options));
        }

        public override Stream GetWriteStream(FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            return OpenWriteStream(options);
        }

        /// <summary>
        /// Returns the cached metadata snapshot when the driver already loaded
        /// it (strict <c>OpenFile</c> / <c>TryOpenFile</c> paid the HEAD); fires
        /// a single HEAD to populate it otherwise.
        /// </summary>
        public override async Task<FileMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
        {
            if (!_isLoaded)
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            // Safe to hand out directly: the snapshot is immutable and replaced
            // wholesale on refresh / write, never mutated in place.
            return _metadata;
        }

        // === IUrlAccessible ===

        public bool IsPublic => SessionInternal.GetIsPublic();

        public Uri GetPublicUrl() => SyncBridge.Run(GetPublicUrlAsync);

        public async Task<Uri> GetPublicUrlAsync(CancellationToken cancellationToken = default)
        {
            if (!await SessionInternal.GetIsPublicAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"Bucket \"{SessionInternal.Client.Bucket}\" is not public. Use GetSignedUrl(TimeSpan) instead.");

            var client = SessionInternal.Client;
            // OCI object names are flat: "/" is part of the name, so the whole
            // name is a single encoded path segment. Namespace and bucket are
            // caller-supplied — encode them too.
            var encodedNamespace = Uri.EscapeDataString(client.Namespace);
            var encodedBucket = Uri.EscapeDataString(client.Bucket);
            var encodedObject = Uri.EscapeDataString(ObjectName);
            return new Uri(
                $"https://objectstorage.{client.Region}.oraclecloud.com/n/{encodedNamespace}/b/{encodedBucket}/o/{encodedObject}");
        }

        public Uri GetSignedUrl(TimeSpan expiresIn)
            => SyncBridge.Run(ct => GetSignedUrlAsync(expiresIn, ct));

        public async Task<Uri> GetSignedUrlAsync(TimeSpan expiresIn, CancellationToken cancellationToken = default)
        {
            if (expiresIn <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            var client = SessionInternal.Client;
            var parName = $"{ObjectName}-{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var timeExpires = DateTime.UtcNow.Add(expiresIn);

            var accessUri = await client.CreatePreauthenticatedReadRequestAsync(ObjectName, parName, timeExpires, cancellationToken).ConfigureAwait(false);
            return new Uri($"https://objectstorage.{client.Region}.oraclecloud.com{accessUri}");
        }

        public override void Dispose()
        {
            if (_lastOpenStream != null)
            {
                _lastOpenStream.Disposed -= OnStreamDisposed;
                _lastOpenStream = null;
            }
            base.Dispose();
        }

        private static string ConcatPath(string parentPath, string name)
        {
            if (string.IsNullOrEmpty(parentPath) || parentPath == "/")
                return "/" + name;
            return parentPath + "/" + name;
        }
    }
}
