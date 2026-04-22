using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileHub.OracleObjectStorage.Internal;

namespace FileHub.OracleObjectStorage
{
    public class OracleObjectStorageFile : FileEntry, IUrlAccessible
    {
        internal const string ChangedAtTag = "_changedAt";

        private readonly OracleObjectStorageDirectory _parent;
        private long _length;
        private DateTime _creationTimeUtc;
        private Dictionary<string, string> _tags;
        private bool _needsRefresh;
        private OciObjectStream _lastOpenStream;

        public override FileDirectory Parent => _parent;
        public override string Path => ConcatPath(_parent.Path, Name);
        public override long Length { get { RefreshIfNeeded(); return _length; } }
        public override DateTime CreationTimeUtc { get { RefreshIfNeeded(); return _creationTimeUtc; } }

        public override DateTime LastWriteTimeUtc
        {
            get
            {
                RefreshIfNeeded();
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
            _needsRefresh = true;
        }

        internal OracleObjectStorageFile(OracleObjectStorageDirectory parent, string name, long length, DateTime? createdUtc)
            : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = length;
            _creationTimeUtc = createdUtc ?? default;
            _needsRefresh = true;
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

        public override Stream GetReadStream()
        {
            RefreshIfNeeded();
            return OpenStream(isWrite: false);
        }

        public override Stream GetWriteStream()
        {
            ThrowIfReadOnly();
            return OpenStream(isWrite: true);
        }

        public override async Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return OpenStream(isWrite: false);
        }

        public override Task<Stream> GetWriteStreamAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(OpenStream(isWrite: true));
        }

        private OciObjectStream OpenStream(bool isWrite)
        {
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
            _needsRefresh = true;
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
                return new OracleObjectStorageFile(ociDir, name);
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

        internal void MarkNeedsRefresh() => _needsRefresh = true;

        internal void EnsureTags()
        {
            if (_tags == null)
                _tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        internal void RefreshIfNeeded(bool forceRefresh = false)
        {
            if (!_needsRefresh && !forceRefresh) return;
            RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        internal async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var head = await SessionInternal.Client.HeadObjectAsync(ObjectName, cancellationToken).ConfigureAwait(false);
            _length = head.ContentLength ?? -1;
            _creationTimeUtc = head.LastModified ?? default;
            _tags = head.OpcMeta != null
                ? new Dictionary<string, string>(head.OpcMeta, StringComparer.OrdinalIgnoreCase)
                : null;
            _needsRefresh = false;
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
