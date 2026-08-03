using FileHub.AmazonS3.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// <see cref="FileEntry"/> backed by an S3 object.
    /// <para>
    /// <b>Thread safety.</b> Not thread-safe. Cached state
    /// (<see cref="Length"/>, <see cref="LastWriteTimeUtc"/>, the metadata
    /// snapshot, the <c>IsLoaded</c> flag, and the single-stream latch) is
    /// mutated from multiple driver paths without locks. A single
    /// <see cref="AmazonS3File"/> instance must be used from one logical
    /// thread at a time. Cross-instance concurrency (different
    /// <see cref="AmazonS3File"/> handles to the same or different objects)
    /// is fine.
    /// </para>
    /// </summary>
    public class AmazonS3File : FileEntry, IUrlAccessible, IRefreshable, IMultipartUploadSignable, ILazyLoad
    {
        /// <summary>S3 minimum for multipart parts (except the last). 5 MiB.</summary>
        internal const long S3MinimumPartSize = 5L * 1024 * 1024;

        /// <summary>S3 maximum parts per upload.</summary>
        internal const int S3MaximumPartCount = 10_000;

        private readonly AmazonS3Directory _parent;
        private long _length;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;
        private bool _isLoaded;
        private AmazonS3FileMetadata _metadata = new AmazonS3FileMetadata();
        private S3FileStreamBase _lastOpenStream;

        /// <summary>
        /// <c>true</c> once the file's state has been loaded from the
        /// store. <c>false</c> on pending stubs from
        /// <c>OpenFile(name, createIfNotExists: true)</c> and after
        /// <see cref="CompleteSignedMultipartUploadAsync"/> — the bytes went
        /// client-to-store, so the local snapshot (<see cref="Length"/>,
        /// metadata) is unknown until the next refresh.
        /// </summary>
        public bool IsLoaded => _isLoaded;

        public override FileDirectory Parent => _parent;
        public override string Path => ConcatPath(_parent.Path, Name);

        /// <summary>
        /// Cached content length. Returns the last known value — call
        /// <see cref="Refresh"/>/<see cref="RefreshAsync"/> to re-sync with
        /// the bucket. Writes through this driver update the cached length
        /// as data is streamed.
        /// </summary>
        public override long Length => _length;

        public override DateTime CreationTimeUtc => _creationTimeUtc;

        /// <summary>
        /// S3's native <c>LastModified</c> from the last HEAD/LIST. Updated
        /// client-side after a successful write. Drivers do not do hidden
        /// I/O in getters.
        /// </summary>
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc;

        internal string ObjectKey => PathUtil.CombineKey(_parent.PrefixInternal, Name);
        internal IS3Session SessionInternal => _parent.SessionInternal;
        internal long LengthInternal { get => _length; set => _length = value; }

        /// <summary>Pending stub — no state loaded yet.</summary>
        internal AmazonS3File(AmazonS3Directory parent, string name) : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = -1;
            _isLoaded = false;
        }

        /// <summary>
        /// Populated with Length/LastModified (typically from LIST —
        /// Metadata not loaded). Callers from HEAD paths should invoke
        /// <see cref="LoadMetadataFromHead"/> afterwards to flip
        /// <see cref="IsLoaded"/> to <c>true</c>.
        /// </summary>
        internal AmazonS3File(AmazonS3Directory parent, string name, long length, DateTime? lastModifiedUtc)
            : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = length;
            _creationTimeUtc = lastModifiedUtc ?? default;
            _lastWriteTimeUtc = lastModifiedUtc ?? default;
            _isLoaded = false;
        }

        // === IRefreshable ===

        public void Refresh() => SyncBridge.Run(ct => RefreshAsync(ct));

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = await SessionInternal.Client.HeadObjectAsync(ObjectKey, cancellationToken).ConfigureAwait(false);
            _length = head.ContentLength ?? -1;
            _creationTimeUtc = head.LastModified ?? default;
            _lastWriteTimeUtc = head.LastModified ?? default;
            LoadMetadataFromHead(head);
        }

        /// <summary>
        /// Driver-internal: flip <see cref="IsLoaded"/> to <c>true</c>
        /// without invoking a HEAD. Used by <c>CreateFile</c> (we just
        /// put an empty object, state is known) and similar paths.
        /// </summary>
        internal void MarkLoaded() => _isLoaded = true;

        internal void LoadMetadataFromHead(S3HeadResult head)
        {
            _metadata = new AmazonS3FileMetadata(
                contentType: head.ContentType,
                cacheControl: head.CacheControl,
                tags: head.UserMetadata,
                storageClass: head.StorageClass,
                serverSideEncryption: head.ServerSideEncryption);
            _isLoaded = true;
        }

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

        public override Stream GetReadStream()
        {
            ThrowIfStreamOpen();
            return Track(new S3ReadStream(this));
        }

        public override Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStreamOpen();
            return Task.FromResult<Stream>(Track(new S3ReadStream(this)));
        }

        private S3ObjectStream OpenWriteStream(S3WriteOptions options)
        {
            ThrowIfStreamOpen();
            AmazonS3FileHub.ValidateMultipartOptions(options?.Multipart, nameof(options));
            return Track(new S3ObjectStream(
                this,
                options,
                options?.Multipart ?? SessionInternal.Multipart));
        }

        /// <summary>
        /// Registers a freshly created stream in the "one stream open per
        /// file" latch. The latch clears when the stream raises Disposed.
        /// </summary>
        private T Track<T>(T stream) where T : S3FileStreamBase
        {
            _lastOpenStream = stream;
            stream.Disposed += OnStreamDisposed;
            return stream;
        }

        private void ThrowIfStreamOpen()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(AmazonS3File));
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
        /// Called at the end of a successful upload to reflect the new
        /// length / last-write timestamp and promote any applied
        /// <see cref="S3WriteOptions"/> into the cached snapshot. Callers that
        /// need the authoritative server timestamp should call <see cref="Refresh"/>.
        /// </summary>
        internal void OnWriteCommitted(long bytesWritten, S3WriteOptions applied = null)
        {
            _length = bytesWritten;
            _lastWriteTimeUtc = DateTime.UtcNow;
            ApplyToMetadata(applied);
            _isLoaded = true;
        }

        // Replace the snapshot with one reflecting the applied options, falling
        // back to the previous value for any field the write didn't set.
        private void ApplyToMetadata(S3WriteOptions options)
        {
            if (options == null) return;
            _metadata = new AmazonS3FileMetadata(
                contentType: options.ContentType ?? _metadata.ContentType,
                cacheControl: options.CacheControl ?? _metadata.CacheControl,
                tags: options.Metadata ?? _metadata.Tags,
                storageClass: options.StorageClass ?? _metadata.StorageClass,
                serverSideEncryption: options.ServerSideEncryption ?? _metadata.ServerSideEncryption);
        }

        // === Mutations ===

        public override void Delete() => SyncBridge.Run(ct => DeleteAsync(ct));

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await SessionInternal.Client.DeleteObjectAsync(ObjectKey, cancellationToken).ConfigureAwait(false);
            _length = -1;
        }

        public override FileEntry Rename(string newName) => SyncBridge.Run(ct => RenameAsync(newName, ct));

        public override async Task<FileEntry> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            // S3 has no atomic rename. Fall back to copy+delete in-place.
            // Always default COPY — source metadata is preserved on the new key.
            ThrowIfReadOnly();
            NestedPath.EnsureLeaf(newName);

            PathUtil.ValidateName(newName);

            // Rename never overwrites — CopyObject would clobber an existing
            // key, so guard with a HEAD. Best-effort, not atomic.
            if (await _parent.ExistsAsync(newName, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(_parent.Path, newName));

            var sourceKey = ObjectKey;
            var destinationKey = PathUtil.CombineKey(_parent.PrefixInternal, newName);
            var client = SessionInternal.Client;

            await client.CopyFromBucketAsync(
                client.Bucket, sourceKey, destinationKey,
                metadataReplace: false,
                options: null,
                cancellationToken).ConfigureAwait(false);
            await client.DeleteObjectAsync(sourceKey, cancellationToken).ConfigureAwait(false);
            Name = newName;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, progress, overwrite, ct));

        public override async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            var newFile = await CopyToAsync(directory, name, progress, overwrite, cancellationToken).ConfigureAwait(false);
            try
            {
                await DeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // Source already gone — move is effectively complete.
            }
            catch (Exception ex)
            {
                throw new PartialMoveException(
                    $"File was copied to \"{newFile.Path}\" but the original at \"{Path}\" could not be deleted. " +
                    "The move is partial — remove the source manually.",
                    sourcePath: Path,
                    destinationPath: newFile.Path,
                    innerException: ex);
            }
            return newFile;
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
            return Task.FromResult<Stream>(OpenWriteStream(NormalizeOptions(options)));
        }

        public override Stream GetWriteStream(FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            return OpenWriteStream(NormalizeOptions(options));
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

        // Normalize FileWriteOptions to S3WriteOptions so internal paths
        // can read StorageClass / SSE slots even when the caller passed the
        // base type.
        internal static S3WriteOptions NormalizeOptions(FileWriteOptions options)
        {
            if (options == null) return null;
            if (options is S3WriteOptions s3) return s3;
            return new S3WriteOptions
            {
                ContentType = options.ContentType,
                CacheControl = options.CacheControl,
                Metadata = options.Metadata,
                StreamPreference = options.StreamPreference,
            };
        }

        public override FileEntry CopyTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, progress, overwrite, ct));

        public override async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory under the destination and
            // recurse with the single leaf so the server-side copy still applies.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                {
                    var deeper = await directory.CreateDirectoryAsync(subPath, cancellationToken).ConfigureAwait(false);
                    return await CopyToAsync(deeper, leaf, progress, overwrite, cancellationToken).ConfigureAwait(false);
                }
                name = leaf;
            }

            if (directory is AmazonS3Directory s3Dir
                && S3SessionTarget.SameCredentials(s3Dir.SessionInternal.Client, SessionInternal.Client))
            {
                PathUtil.ValidateName(name);
                // Refuse copy/move onto the exact same object (same bucket + same
                // key) — a self-copy is a no-op that S3 itself rejects, and a
                // self-move would then delete the object it just "moved".
                if (IsSameBucket(s3Dir)
                    && string.Equals(PathUtil.CombineKey(s3Dir.PrefixInternal, name), ObjectKey, StringComparison.Ordinal))
                    throw new FileAlreadyExistsException($"Cannot copy \"{Path}\" onto itself.", Path);
                // overwrite: false must not clobber an existing object. S3 PutObject
                // (and CopyObject) always overwrite, so guard with an explicit HEAD.
                // Best-effort — not atomic against a concurrent writer.
                if (!overwrite && await s3Dir.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                    throw new FileAlreadyExistsException(PathUtil.JoinDisplay(s3Dir.Path, name));
                // Ensure we know the source size — without this, a stub created
                // via OpenFile(name, createIfNotExists: true) that was never
                // refreshed would propagate _length = -1 into the new file,
                // making consumers see a "missing" object.
                if (!_isLoaded)
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                var destinationKey = PathUtil.CombineKey(s3Dir.PrefixInternal, name);
                var sourceClient = SessionInternal.Client;
                var destClient = s3Dir.SessionInternal.Client;
                // Issue CopyObject via the destination client — its endpoint
                // is the destination region, which is the only endpoint that
                // S3 accepts for cross-region routing. Same-region copies
                // are indistinguishable. Always default COPY — destination
                // inherits the source's metadata.
                await destClient.CopyFromBucketAsync(
                    sourceClient.Bucket,
                    ObjectKey,
                    destinationKey,
                    metadataReplace: false,
                    options: null,
                    cancellationToken).ConfigureAwait(false);
                // Server-side copy is a single atomic API call — no byte stream
                // to meter. Report one completed tick so a progress consumer
                // sees the transfer finish.
                progress?.Report(new TransferStatus(_length, _length));
                return new AmazonS3File(s3Dir, name, _length, _lastWriteTimeUtc);
            }
            return await base.CopyToAsync(directory, name, progress, overwrite, cancellationToken).ConfigureAwait(false);
        }

        // === IUrlAccessible ===

        public bool IsPublic => SessionInternal.GetIsPublic();

        public Uri GetPublicUrl() => SyncBridge.Run(ct => GetPublicUrlAsync(ct));

        public async Task<Uri> GetPublicUrlAsync(CancellationToken cancellationToken = default)
        {
            if (!await SessionInternal.GetIsPublicAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"Bucket \"{SessionInternal.Client.Bucket}\" is not public. Use GetSignedUrl(TimeSpan) instead.");

            var client = SessionInternal.Client;
            // Encode each segment separately: a key containing a literal "%2F"
            // must survive as "%252F", not turn into a path separator.
            var encodedKey = string.Join("/", Array.ConvertAll(ObjectKey.Split('/'), Uri.EscapeDataString));
            return new Uri($"https://{client.Bucket}.s3.{client.Region}.amazonaws.com/{encodedKey}");
        }

        public Uri GetSignedUrl(TimeSpan expiresIn) => SyncBridge.Run(ct => GetSignedUrlAsync(expiresIn, ct));

        public async Task<Uri> GetSignedUrlAsync(TimeSpan expiresIn, CancellationToken cancellationToken = default)
        {
            if (expiresIn <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            var expiresUtc = DateTime.UtcNow.Add(expiresIn);
            var url = await SessionInternal.Client.GetPreSignedUrlAsync(ObjectKey, expiresUtc, cancellationToken).ConfigureAwait(false);
            return new Uri(url);
        }

        // === IMultipartUploadable ===

        public long MinimumPartSize => S3MinimumPartSize;

        // === IMultipartUploadSignable ===

        public SignedMultipartUpload BeginSignedMultipartUpload(MultipartUploadSpec spec, TimeSpan expiresIn, FileWriteOptions options = null)
            => SyncBridge.Run(ct => BeginSignedMultipartUploadAsync(spec, expiresIn, options, ct));

        public void CompleteSignedMultipartUpload(string uploadId, IReadOnlyList<UploadedPart> parts)
            => SyncBridge.Run(ct => CompleteSignedMultipartUploadAsync(uploadId, parts, ct));

        public void AbortSignedMultipartUpload(string uploadId)
            => SyncBridge.Run(ct => AbortSignedMultipartUploadAsync(uploadId, ct));

        public async Task<SignedMultipartUpload> BeginSignedMultipartUploadAsync(
            MultipartUploadSpec spec,
            TimeSpan expiresIn,
            FileWriteOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            ValidateSpec(spec);
            if (expiresIn <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            var client = SessionInternal.Client;
            var s3Options = NormalizeOptions(options);
            var uploadId = await client.BeginMultipartUploadAsync(
                ObjectKey, s3Options, cancellationToken).ConfigureAwait(false);

            // The metadata is bound to the object at CreateMultipartUpload, so
            // reflect it in the cached snapshot now — it becomes real once the
            // client completes the upload (and is moot if the upload is aborted,
            // since the object never materializes).
            ApplyToMetadata(s3Options);

            var expiresUtc = DateTime.UtcNow.Add(expiresIn);
            var signedParts = new List<SignedPart>(spec.PartCount);
            for (int i = 1; i <= spec.PartCount; i++)
            {
                var url = await client.GetPreSignedUploadPartUrlAsync(ObjectKey, uploadId, i, expiresUtc, cancellationToken).ConfigureAwait(false);
                signedParts.Add(new SignedPart(i, url, spec.GetPartLength(i)));
            }
            return new SignedMultipartUpload(uploadId, spec, signedParts);
        }

        public async Task CompleteSignedMultipartUploadAsync(
            string uploadId,
            IReadOnlyList<UploadedPart> parts,
            CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (string.IsNullOrEmpty(uploadId)) throw new ArgumentException("UploadId cannot be null or empty.", nameof(uploadId));
            if (parts == null) throw new ArgumentNullException(nameof(parts));

            var completed = new List<S3CompletedPart>(parts.Count);
            foreach (var p in parts)
                completed.Add(new S3CompletedPart { PartNumber = p.PartNumber, ETag = p.ETag });

            await SessionInternal.Client.CompleteMultipartUploadAsync(ObjectKey, uploadId, completed, cancellationToken).ConfigureAwait(false);
            // The bytes never passed through this process — only the server
            // knows the final size. Invalidate the snapshot so the next
            // metadata access lazy-refreshes instead of reporting stale Length.
            _length = -1;
            _lastWriteTimeUtc = DateTime.UtcNow;
            _isLoaded = false;
        }

        public Task AbortSignedMultipartUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (string.IsNullOrEmpty(uploadId)) throw new ArgumentException("UploadId cannot be null or empty.", nameof(uploadId));
            return SessionInternal.Client.AbortMultipartUploadAsync(ObjectKey, uploadId, cancellationToken);
        }

        private static void ValidateSpec(MultipartUploadSpec spec)
        {
            if (spec.PartCount > S3MaximumPartCount)
                throw new ArgumentException($"S3 allows at most {S3MaximumPartCount} parts per upload (got {spec.PartCount}).", nameof(spec));
            // Intermediate parts must be >= 5 MiB; the last part may be smaller.
            if (spec.PartCount > 1 && spec.PartSize < S3MinimumPartSize)
                throw new ArgumentException($"S3 requires parts of at least {S3MinimumPartSize} bytes except the last (spec.PartSize = {spec.PartSize}).", nameof(spec));
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

        // True when the target directory resolves to the same physical bucket as
        // this file — the precondition for a server-side copy and for the
        // self-move/copy guard (a matching key in a different bucket is a
        // different object).
        private bool IsSameBucket(AmazonS3Directory dir)
            => string.Equals(dir.SessionInternal.Client.Bucket, SessionInternal.Client.Bucket, StringComparison.Ordinal);

        private static string ConcatPath(string parentPath, string name)
        {
            if (string.IsNullOrEmpty(parentPath) || parentPath == "/")
                return "/" + name;
            return parentPath + "/" + name;
        }
    }
}
