using FileHub.Ftp.Internal;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        public void Refresh() => SyncBridge.Run(RefreshAsync);

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

        public override bool Exists() => SyncBridge.Run(ExistsAsync);

        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await SessionInternal.Client.FileExistsAsync(FullPath, cancellationToken).ConfigureAwait(false);
        }

        // === Streams ===

        public override Stream GetReadStream() => SyncBridge.Run(GetReadStreamAsync);

        public override async Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await OpenStreamAsync(isWrite: false, cancellationToken).ConfigureAwait(false);
        }

        // StreamPreference is ignored: FTP writes stream straight over the data
        // connection, so there is no single-request vs multipart distinction.
        public override Stream GetWriteStream(FileWriteOptions options = null)
            => SyncBridge.Run(ct => GetWriteStreamAsync(options, ct));

        public override async Task<Stream> GetWriteStreamAsync(FileWriteOptions options = null, CancellationToken cancellationToken = default)
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

        // === Buffered writes: verify-and-retry ===

        // A single FTP data transfer can silently drop its tail when the
        // passive data channel lands on stale port state (observed as a 256 KiB
        // upload landing as 192 KiB — exactly one 64 KiB socket block short).
        // The truncation is non-deterministic and hits any transfer regardless
        // of the write API (raw STOR or FluentFTP's high-level upload). Because
        // SetBytes/SetText hold the whole payload in memory, the driver can read
        // the stored size back and replay the STOR until it matches; the
        // streaming GetWriteStream path cannot, since its source is consumed
        // once. Constant memory: the caller's buffer is reused, never copied.
        private const int MaxUploadAttempts = 4;

        public override void SetBytes(byte[] buffer, FileWriteOptions options = null)
            => SyncBridge.Run(ct => SetBytesAsync(buffer, options, ct));

        public override void SetText(string content, Encoding encoding = null, FileWriteOptions options = null)
            => SyncBridge.Run(ct => SetTextAsync(content, encoding, options, ct));

        public override async Task SetTextAsync(string content, Encoding encoding = null, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (content == null) throw new ArgumentNullException(nameof(content));
            var bytes = (encoding ?? Encoding.UTF8).GetBytes(content);
            await SetBytesAsync(bytes, options, cancellationToken).ConfigureAwait(false);
        }

        public override async Task SetBytesAsync(byte[] buffer, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            cancellationToken.ThrowIfCancellationRequested();
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            // The stream verifies the stored size on close and throws
            // FtpTransferTruncatedException on a silent tail-truncation. Since
            // the whole payload is in hand, replay the STOR when that happens.
            // A retry that succeeds is a met guarantee, so it stays quiet — but
            // if every attempt still loses the tail the exception is rethrown to
            // the caller. Truncation is never swallowed: the caller either gets
            // a fully-committed file or an exception, never a short file.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var stream = await GetWriteStreamAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false))
                    {
                        await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }
                catch (FtpTransferTruncatedException)
                {
                    // Transient data-channel tail loss. Replay while attempts
                    // remain; once they are exhausted, surface the failure.
                    if (attempt >= MaxUploadAttempts) throw;
                }
            }
        }

        // === Mutations ===

        public override void Delete() => SyncBridge.Run(DeleteAsync);

        public override async Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            
            try
            {
                await SessionInternal.Client.DeleteFileAsync(FullPath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
            }
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
            await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            // Rename never overwrites — behaviour on an existing target is
            // server-dependent, so check first and fail with a clear exception.
            if (await _parent.ExistsAsync(newName, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(_parent.Path, newName));

            var destination = FtpPathUtil.ResolveSafeChildPath(_parent.RootPathInternal, _parent.PathInternal, newName);
            await SessionInternal.Client.RenameAsync(FullPath, destination, cancellationToken).ConfigureAwait(false);

            Name = newName;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
            => SyncBridge.Run(ct => MoveToAsync(directory, name, progress, overwrite, ct));

        public override async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
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

            if (directory is FtpDirectory ftpDir
                && FtpSessionTarget.SameConnection(ftpDir.SessionInternal.Client, SessionInternal.Client))
            {
                PathUtil.ValidateName(name);
                await SessionInternal.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                // overwrite: false must not clobber an existing entry — many FTP
                // servers reject RNTO onto an existing path anyway, but check
                // explicitly so the failure is a clear FileAlreadyExistsException.
                if (!overwrite && await ftpDir.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                    throw new FileAlreadyExistsException(PathUtil.JoinDisplay(ftpDir.Path, name));
                var destination = FtpPathUtil.ResolveSafeChildPath(ftpDir.RootPathInternal, ftpDir.PathInternal, name);
                // Same connection + same resolved path means moving onto itself.
                if (string.Equals(destination, FullPath, StringComparison.Ordinal))
                    throw new FileAlreadyExistsException($"Cannot move \"{Path}\" onto itself.", Path);
                await SessionInternal.Client.RenameAsync(FullPath, destination, cancellationToken).ConfigureAwait(false);
                progress?.Report(new TransferStatus(_length, _length));
                return new FtpFile(ftpDir, name, _length, _lastWriteTimeUtc, _creationTimeUtc);
            }

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

        public override FileEntry CopyTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
            => SyncBridge.Run(ct => CopyToAsync(directory, name, progress, overwrite, ct));

        /// <summary>
        /// FTP has no server-side copy command. When source and destination
        /// share the same connection, the copy is spilled through a temporary
        /// file on local disk and runs strictly sequentially (download fully,
        /// close the data channel, then upload) — a single FTP connection
        /// supports only one data transfer at a time, so the base
        /// stream-to-stream copy would require two simultaneous data channels.
        /// Both legs stream in chunks; memory usage is constant regardless of
        /// file size. Cross-connection copies still stream directly.
        /// </summary>
        public override async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
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

            if (directory is FtpDirectory ftpDir
                && FtpSessionTarget.SameConnection(ftpDir.SessionInternal.Client, SessionInternal.Client))
            {
                PathUtil.ValidateName(name);
                // Same connection + same resolved path means copying onto itself.
                if (string.Equals(FtpPathUtil.ResolveSafeChildPath(ftpDir.RootPathInternal, ftpDir.PathInternal, name), FullPath, StringComparison.Ordinal))
                    throw new FileAlreadyExistsException($"Cannot copy \"{Path}\" onto itself.", Path);
                // overwrite: false must not clobber the destination — the upload
                // below (STOR) would replace it. Check before spilling to temp.
                if (!overwrite && await ftpDir.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                    throw new FileAlreadyExistsException(PathUtil.JoinDisplay(ftpDir.Path, name));
                var tempPath = System.IO.Path.GetTempFileName();
                try
                {
                    using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await CopyToStreamAsync(temp, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    var newFile = await ftpDir.CreateFileAsync(name, cancellationToken).ConfigureAwait(false);
                    using (var temp = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                    {
                        // Meter progress on the upload leg — it's the transfer the
                        // caller waits on; the download-to-temp leg is local disk.
                        await newFile.CopyFromStreamAsync(temp, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    return newFile;
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { /* best effort — temp dir cleanup */ }
                }
            }

            return await base.CopyToAsync(directory, name, progress, overwrite, cancellationToken).ConfigureAwait(false);
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
    }
}
