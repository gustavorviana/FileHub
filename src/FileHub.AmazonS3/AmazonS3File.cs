using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileHub.AmazonS3.Internal;

namespace FileHub.AmazonS3
{
    public class AmazonS3File : FileEntry, IUrlAccessible, IRefreshable, IMultipartUploadable, IMultipartUploadSignable, IMetadataAware, ILazyLoad
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
        private readonly AmazonS3FileMetadata _metadata = new AmazonS3FileMetadata();
        private S3ObjectStream _lastOpenStream;

        /// <summary>
        /// <c>true</c> once the file's state has been loaded from the
        /// store. <c>false</c> on pending stubs from
        /// <c>OpenFile(name, createIfNotExists: true)</c>.
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

        /// <summary>
        /// Mutable snapshot of the file's S3 metadata (tags, storage
        /// class, content-type, SSE). Populated by
        /// <see cref="Refresh"/> / <see cref="RefreshAsync"/> and by
        /// <see cref="AmazonS3Directory.TryOpenFile"/>. Mutate freely — on
        /// the next alteration op (<see cref="SetBytes"/>,
        /// <see cref="CopyTo(FileDirectory, string)"/>,
        /// <see cref="MoveTo(FileDirectory, string)"/>, stream commit),
        /// if <see cref="FileMetadata.IsModified"/> is <c>true</c>, the
        /// driver applies the staged values via
        /// <c>MetadataDirective = REPLACE</c> on S3 and clears the flag.
        ///
        /// <para>
        /// Canonical pattern to update metadata without re-uploading bytes:
        /// <code>
        /// file.Metadata.StorageClass = "GLACIER";
        /// file.CopyTo(file.Parent, file.Name);   // self-copy applies the change
        /// </code>
        /// </para>
        /// </summary>
        public AmazonS3FileMetadata Metadata => _metadata;

        FileMetadata IMetadataAware.Metadata => _metadata;

        internal string ObjectKey => S3PathUtil.CombineObjectKey(_parent.PrefixInternal, Name);
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
            _metadata.LoadSynced(
                tags: head.UserMetadata,
                storageClass: head.StorageClass,
                contentType: head.ContentType,
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

        public override Stream GetReadStream() => OpenStream(isWrite: false);

        public override Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenStream(isWrite: false));
        }

        public override Stream GetWriteStream()
        {
            ThrowIfReadOnly();
            return OpenStream(isWrite: true);
        }

        public override Task<Stream> GetWriteStreamAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenStream(isWrite: true));
        }

        private S3ObjectStream OpenStream(bool isWrite)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(AmazonS3File));
            if (_lastOpenStream != null)
                throw new InvalidOperationException("A stream is already open for this file. Dispose it before opening another.");

            var stream = new S3ObjectStream(this, isWrite);
            _lastOpenStream = stream;
            stream.Disposed += OnStreamDisposed;
            return stream;
        }

        private void OnStreamDisposed(object sender, EventArgs e)
        {
            if (_lastOpenStream != null)
                _lastOpenStream.Disposed -= OnStreamDisposed;
            _lastOpenStream = null;
        }

        /// <summary>
        /// Called at the end of a successful upload to reflect the new
        /// length / last-write timestamp, and to mark the metadata
        /// snapshot as synced with the store (since the PUT just applied
        /// the staged values). Callers that need the authoritative server
        /// timestamp should call <see cref="Refresh"/>.
        /// </summary>
        internal void OnWriteCommitted(long bytesWritten)
        {
            _length = bytesWritten;
            _lastWriteTimeUtc = DateTime.UtcNow;
            _metadata.MarkSynced();
            _isLoaded = true;
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
            // If the caller staged metadata changes (Metadata.IsModified),
            // apply with REPLACE; otherwise preserve source metadata.
            ThrowIfReadOnly();
            S3PathUtil.ValidateName(newName);
            var sourceKey = ObjectKey;
            var destinationKey = S3PathUtil.CombineObjectKey(_parent.PrefixInternal, newName);
            var client = SessionInternal.Client;

            var replace = _metadata.IsModified;
            await client.CopyFromBucketAsync(
                client.Bucket, sourceKey, destinationKey,
                metadataReplace: replace,
                contentType: replace ? _metadata.ContentType : null,
                userMetadata: replace && _metadata.Tags.Count > 0 ? (IReadOnlyDictionary<string, string>)_metadata.Tags : null,
                storageClass: replace ? _metadata.StorageClass : null,
                serverSideEncryption: replace ? _metadata.ServerSideEncryption : null,
                cancellationToken).ConfigureAwait(false);
            await client.DeleteObjectAsync(sourceKey, cancellationToken).ConfigureAwait(false);
            if (replace) _metadata.MarkSynced();
            Name = newName;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, ct));

        public override async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            var newFile = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
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

        public override FileEntry CopyTo(FileDirectory directory, string name)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, ct));

        public override async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            if (directory is AmazonS3Directory s3Dir
                && S3SessionTarget.SameCredentials(s3Dir.SessionInternal.Client, SessionInternal.Client))
            {
                S3PathUtil.ValidateName(name);
                var destinationKey = S3PathUtil.CombineObjectKey(s3Dir.PrefixInternal, name);
                var sourceClient = SessionInternal.Client;
                var destClient = s3Dir.SessionInternal.Client;
                var replace = _metadata.IsModified;
                // Issue CopyObject via the destination client — its endpoint
                // is the destination region, which is the only endpoint that
                // S3 accepts for cross-region routing. Same-region copies
                // are indistinguishable. If the source has staged metadata
                // changes, apply with REPLACE; otherwise the SDK default
                // (COPY) makes the destination inherit source state.
                await destClient.CopyFromBucketAsync(
                    sourceClient.Bucket,
                    ObjectKey,
                    destinationKey,
                    metadataReplace: replace,
                    contentType: replace ? _metadata.ContentType : null,
                    userMetadata: replace && _metadata.Tags.Count > 0 ? (IReadOnlyDictionary<string, string>)_metadata.Tags : null,
                    storageClass: replace ? _metadata.StorageClass : null,
                    serverSideEncryption: replace ? _metadata.ServerSideEncryption : null,
                    cancellationToken).ConfigureAwait(false);
                if (replace) _metadata.MarkSynced();
                return new AmazonS3File(s3Dir, name, _length, _lastWriteTimeUtc);
            }
            return await base.CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
        }

        // === IUrlAccessible ===

        public bool IsPublic => SessionInternal.GetIsPublic();

        public Uri GetPublicUrl()
        {
            if (!SessionInternal.GetIsPublic())
                throw new InvalidOperationException(
                    $"Bucket \"{SessionInternal.Client.Bucket}\" is not public. Use GetSignedUrl(TimeSpan) instead.");

            var client = SessionInternal.Client;
            var encodedKey = Uri.EscapeDataString(ObjectKey).Replace("%2F", "/");
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

        public Stream GetMultipartWriteStream() => SyncBridge.Run(ct => GetMultipartWriteStreamAsync(ct));

        public async Task<Stream> GetMultipartWriteStreamAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            // If the caller staged metadata changes, apply them on
            // InitiateMultipartUpload; otherwise the object gets defaults.
            var md = _metadata;
            var dirty = md.IsModified;
            var uploadId = await SessionInternal.Client.BeginMultipartUploadAsync(
                ObjectKey,
                contentType: dirty ? md.ContentType : null,
                userMetadata: dirty && md.Tags.Count > 0 ? (IReadOnlyDictionary<string, string>)md.Tags : null,
                storageClass: dirty ? md.StorageClass : null,
                serverSideEncryption: dirty ? md.ServerSideEncryption : null,
                cancellationToken).ConfigureAwait(false);
            return new S3MultipartWriteStream(this, uploadId);
        }

        // === IMultipartUploadSignable ===

        public SignedMultipartUpload BeginSignedMultipartUpload(MultipartUploadSpec spec, TimeSpan expiresIn)
            => SyncBridge.Run(ct => BeginSignedMultipartUploadAsync(spec, expiresIn, ct));

        public void CompleteSignedMultipartUpload(string uploadId, IReadOnlyList<UploadedPart> parts)
            => SyncBridge.Run(ct => CompleteSignedMultipartUploadAsync(uploadId, parts, ct));

        public void AbortSignedMultipartUpload(string uploadId)
            => SyncBridge.Run(ct => AbortSignedMultipartUploadAsync(uploadId, ct));

        public async Task<SignedMultipartUpload> BeginSignedMultipartUploadAsync(
            MultipartUploadSpec spec,
            TimeSpan expiresIn,
            CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            ValidateSpec(spec);
            if (expiresIn <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            var client = SessionInternal.Client;
            // If the caller staged metadata changes, apply them on the
            // InitiateMultipartUpload — S3 records these on the upload
            // and they stick when the client commits via the presigned
            // URLs. Otherwise the resulting object takes bucket defaults.
            var md = _metadata;
            var dirty = md.IsModified;
            var uploadId = await client.BeginMultipartUploadAsync(
                ObjectKey,
                contentType: dirty ? md.ContentType : null,
                userMetadata: dirty && md.Tags.Count > 0 ? (IReadOnlyDictionary<string, string>)md.Tags : null,
                storageClass: dirty ? md.StorageClass : null,
                serverSideEncryption: dirty ? md.ServerSideEncryption : null,
                cancellationToken).ConfigureAwait(false);

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
            _lastWriteTimeUtc = DateTime.UtcNow;
            _metadata.MarkSynced();
            _isLoaded = true;
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

        private static string ConcatPath(string parentPath, string name)
        {
            if (string.IsNullOrEmpty(parentPath) || parentPath == "/")
                return "/" + name;
            return parentPath + "/" + name;
        }
    }
}
