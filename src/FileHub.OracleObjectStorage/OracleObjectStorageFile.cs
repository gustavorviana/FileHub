using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage
{
    public class OracleObjectStorageFile : FileEntry, IUrlAccessible, IRefreshable
    {
        internal const string ChangedAtTag = "_changedAt";

        private readonly OracleObjectStorageDirectory _parent;
        private long _length;
        private DateTime _creationTimeUtc;
        private Dictionary<string, string> _tags;
        private OciObjectStream _lastOpenStream;

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
        /// Cached last-write timestamp. Reads from the object's
        /// <see cref="ChangedAtTag"/> metadata when present, otherwise falls
        /// back to <see cref="CreationTimeUtc"/>. Drivers do not do hidden
        /// I/O in getters.
        /// </summary>
        public override DateTime LastWriteTimeUtc
        {
            get
            {
                if (_tags != null && _tags.TryGetValue(ChangedAtTag, out var value)
                    && DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    return parsed;
                return _creationTimeUtc;
            }
        }

        internal string ObjectName => OciPathUtil.CombineObjectName(_parent.PrefixInternal, Name);
        internal IOciSession SessionInternal => _parent.SessionInternal;
        internal long LengthInternal { get => _length; set => _length = value; }
        internal Dictionary<string, string> TagsInternal => _tags;

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

        public void Refresh() => RefreshAsync().GetAwaiter().GetResult();

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var head = await SessionInternal.Client.HeadObjectAsync(ObjectName, cancellationToken).ConfigureAwait(false);
            _length = head.ContentLength ?? -1;
            _creationTimeUtc = head.LastModified ?? default;
            _tags = head.OpcMeta != null
                ? new Dictionary<string, string>(head.OpcMeta, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        // === Exists (sync delegates to async) ===

        public override bool Exists() => ExistsAsync().GetAwaiter().GetResult();

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

        public override Stream GetWriteStream()
        {
            ThrowIfReadOnly();
            return OpenStream(isWrite: true);
        }

        public override Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenStream(isWrite: false));
        }

        public override Task<Stream> GetWriteStreamAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenStream(isWrite: true));
        }

        private OciObjectStream OpenStream(bool isWrite)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(OracleObjectStorageFile));
            if (_lastOpenStream != null)
                throw new InvalidOperationException("A stream is already open for this file. Dispose it before opening another.");

            var stream = new OciObjectStream(this, isWrite);
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
        /// Called by <see cref="OciObjectStream"/> at the end of a committed
        /// upload so the file reflects the new length and modification time
        /// without forcing a server round-trip. The timestamp is set
        /// client-side via the <see cref="ChangedAtTag"/> metadata; callers
        /// that need the authoritative server timestamp should call
        /// <see cref="Refresh"/>.
        /// </summary>
        internal void OnWriteCommitted(long bytesWritten, string timestampTagValue)
        {
            _length = bytesWritten;
            EnsureTags();
            _tags[ChangedAtTag] = timestampTagValue;
        }

        // === Mutations (sync delegates to async) ===

        public override void Delete() => DeleteAsync().GetAwaiter().GetResult();

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await SessionInternal.Client.DeleteObjectAsync(ObjectName, cancellationToken).ConfigureAwait(false);
            _length = -1;
        }

        public override FileEntry Rename(string newName) => RenameAsync(newName).GetAwaiter().GetResult();

        public override async Task<FileEntry> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            OciPathUtil.ValidateName(newName);
            var destinationObject = OciPathUtil.CombineObjectName(_parent.PrefixInternal, newName);

            await SessionInternal.Client.RenameObjectAsync(ObjectName, destinationObject, cancellationToken).ConfigureAwait(false);

            Name = newName;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name)
            => MoveToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            var newFile = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(cancellationToken).ConfigureAwait(false);
            return newFile;
        }

        public override FileEntry CopyTo(FileDirectory directory, string name)
            => CopyToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            if (directory is OracleObjectStorageDirectory ociDir
                && OciSessionTarget.SameCredentials(ociDir.SessionInternal.Client, SessionInternal.Client))
            {
                OciPathUtil.ValidateName(name);
                var destinationObject = OciPathUtil.CombineObjectName(ociDir.PrefixInternal, name);
                var destClient = ociDir.SessionInternal.Client;
                await SessionInternal.Client.CopyObjectAsync(
                    ObjectName,
                    destClient.Namespace,
                    destClient.Bucket,
                    destClient.Region,
                    destinationObject,
                    cancellationToken).ConfigureAwait(false);
                // Propagate what we know — content is identical, so length matches.
                return new OracleObjectStorageFile(ociDir, name, _length, _creationTimeUtc);
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
            var encodedObject = Uri.EscapeDataString(ObjectName);
            return new Uri(
                $"https://objectstorage.{client.Region}.oraclecloud.com/n/{client.Namespace}/b/{client.Bucket}/o/{encodedObject}");
        }

        public Uri GetSignedUrl(TimeSpan expiresIn)
            => GetSignedUrlAsync(expiresIn).GetAwaiter().GetResult();

        public async Task<Uri> GetSignedUrlAsync(TimeSpan expiresIn, CancellationToken cancellationToken = default)
        {
            if (expiresIn <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(expiresIn), "Expiration must be positive.");

            var client = SessionInternal.Client;
            var parName = $"filehub-{Guid.NewGuid():N}";
            var timeExpires = DateTime.UtcNow.Add(expiresIn);

            var accessUri = await client.CreatePreauthenticatedReadRequestAsync(ObjectName, parName, timeExpires, cancellationToken).ConfigureAwait(false);
            return new Uri($"https://objectstorage.{client.Region}.oraclecloud.com{accessUri}");
        }

        // === Internal ===

        internal void EnsureTags()
        {
            if (_tags == null)
                _tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
