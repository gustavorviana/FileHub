using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileHub.Ftp.Internal;

namespace FileHub.Ftp
{
    public class FtpFile : FileEntry, IRefreshable
    {
        private readonly FtpDirectory _parent;
        private long _length;
        private DateTime _creationTimeUtc;
        private DateTime _lastWriteTimeUtc;
        private FtpStream _lastOpenStream;

        public override FileDirectory Parent => _parent;
        public override string Path => FtpPathUtil.Combine(_parent.Path, Name);

        /// <summary>
        /// Cached content length. Returns the last known value — call
        /// <see cref="Refresh"/> or <see cref="RefreshAsync"/> to re-sync with
        /// the server. Writes through this driver update the cached length at
        /// stream dispose time, so the common write-then-read flow works
        /// without an explicit refresh.
        /// </summary>
        public override long Length => _length;

        /// <summary>Cached creation timestamp. See <see cref="Length"/> for refresh semantics.</summary>
        public override DateTime CreationTimeUtc => _creationTimeUtc;

        /// <summary>Cached last-write timestamp. See <see cref="Length"/> for refresh semantics.</summary>
        public override DateTime LastWriteTimeUtc => _lastWriteTimeUtc;

        internal string FullPath => FtpPathUtil.Combine(_parent.PathInternal, Name);
        internal IFtpSession SessionInternal => _parent.SessionInternal;
        internal long LengthInternal { get => _length; set => _length = value; }

        internal FtpFile(FtpDirectory parent, string name) : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = -1;
        }

        internal FtpFile(FtpDirectory parent, string name, long length, DateTime modifiedUtc, DateTime createdUtc)
            : base(name)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _length = length;
            _lastWriteTimeUtc = modifiedUtc;
            _creationTimeUtc = createdUtc == default ? modifiedUtc : createdUtc;
        }

        // === IRefreshable ===

        public void Refresh() => RefreshAsync().GetAwaiter().GetResult();

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var info = await SessionInternal.Client.StatAsync(FullPath, cancellationToken).ConfigureAwait(false);
            if (info == null)
            {
                _length = -1;
                _creationTimeUtc = default;
                _lastWriteTimeUtc = default;
            }
            else
            {
                _length = info.Size;
                _creationTimeUtc = info.CreatedUtc == default ? info.ModifiedUtc : info.CreatedUtc;
                _lastWriteTimeUtc = info.ModifiedUtc;
            }
        }

        // === Existence ===

        public override bool Exists() => ExistsAsync().GetAwaiter().GetResult();

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await SessionInternal.Client.FileExistsAsync(FullPath, cancellationToken).ConfigureAwait(false);
        }

        // === Streams ===

        public override Stream GetReadStream() => GetReadStreamAsync().GetAwaiter().GetResult();

        public override async Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await OpenStreamAsync(isWrite: false, cancellationToken).ConfigureAwait(false);
        }

        public override Stream GetWriteStream() => GetWriteStreamAsync().GetAwaiter().GetResult();

        public override async Task<Stream> GetWriteStreamAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await OpenStreamAsync(isWrite: true, cancellationToken).ConfigureAwait(false);
        }

        private async Task<Stream> OpenStreamAsync(bool isWrite, CancellationToken cancellationToken)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FtpFile));
            if (_lastOpenStream != null)
                throw new InvalidOperationException("A stream is already open for this file. Dispose it before opening another.");

            var raw = isWrite
                ? await SessionInternal.Client.OpenWriteAsync(FullPath, cancellationToken).ConfigureAwait(false)
                : await SessionInternal.Client.OpenReadAsync(FullPath, 0, cancellationToken).ConfigureAwait(false);

            var wrapped = new FtpStream(raw, this, isWrite);
            _lastOpenStream = wrapped;
            wrapped.Disposed += OnStreamDisposed;
            return wrapped;
        }

        private void OnStreamDisposed(object sender, EventArgs e)
        {
            if (_lastOpenStream != null)
                _lastOpenStream.Disposed -= OnStreamDisposed;
            _lastOpenStream = null;
        }

        /// <summary>
        /// Called by <see cref="FtpStream"/> at the end of a write so the file
        /// reflects the new length without forcing a server round-trip. The
        /// write timestamp is updated client-side too; callers that need the
        /// authoritative server timestamp should call <see cref="Refresh"/>.
        /// </summary>
        internal void OnWriteCompleted(long bytesWritten)
        {
            _length = bytesWritten;
            _lastWriteTimeUtc = DateTime.UtcNow;
            if (_creationTimeUtc == default)
                _creationTimeUtc = _lastWriteTimeUtc;
        }

        // === Mutations ===

        public override void Delete() => DeleteAsync().GetAwaiter().GetResult();

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await SessionInternal.Client.DeleteFileAsync(FullPath, cancellationToken).ConfigureAwait(false);
            _length = -1;
        }

        public override FileEntry Rename(string newName) => RenameAsync(newName).GetAwaiter().GetResult();

        public override async Task<FileEntry> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            FtpPathUtil.ValidateName(newName);
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var destination = FtpPathUtil.ResolveSafeChildPath(_parent.RootPathInternal, _parent.PathInternal, newName);
            await SessionInternal.Client.RenameAsync(FullPath, destination, cancellationToken).ConfigureAwait(false);

            Name = newName;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name)
            => MoveToAsync(directory, name).GetAwaiter().GetResult();

        public override async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();

            if (directory is FtpDirectory ftpDir
                && FtpSessionTarget.SameConnection(ftpDir.SessionInternal.Client, SessionInternal.Client))
            {
                FtpPathUtil.ValidateName(name);
                await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var destination = FtpPathUtil.ResolveSafeChildPath(ftpDir.RootPathInternal, ftpDir.PathInternal, name);
                await SessionInternal.Client.RenameAsync(FullPath, destination, cancellationToken).ConfigureAwait(false);
                return new FtpFile(ftpDir, name, _length, _lastWriteTimeUtc, _creationTimeUtc);
            }

            var newFile = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(cancellationToken).ConfigureAwait(false);
            return newFile;
        }

        public override FileEntry CopyTo(FileDirectory directory, string name)
            => CopyToAsync(directory, name).GetAwaiter().GetResult();

        // CopyTo intentionally falls back to the base implementation (stream copy).
        // FTP has no server-side copy command, even within the same connection.

        public override void Dispose()
        {
            if (_lastOpenStream != null)
            {
                _lastOpenStream.Disposed -= OnStreamDisposed;
                _lastOpenStream = null;
            }
            base.Dispose();
        }
    }
}
