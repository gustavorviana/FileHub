using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Local
{
    public class LocalFile : FileEntry
    {
        private FileInfo _info;

        public override string Path => System.IO.Path.Combine(Parent.Path, Name);
        public override FileDirectory Parent { get; }
        public override long Length => RefreshInfo().Length;
        public override DateTime CreationTimeUtc => RefreshInfo().CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => RefreshInfo().LastWriteTimeUtc;

        internal LocalFile(FileDirectory parent, string name) : base(name)
        {
            Parent = parent;
        }

        /// <summary>
        /// Create a <see cref="LocalFile"/> reference pointing at <paramref name="fileName"/>
        /// inside <paramref name="directory"/>. The file itself is not created on
        /// disk — call <see cref="FileEntry.SetText(string, System.Text.Encoding, FileWriteOptions)"/>, <see cref="FileEntry.SetBytes(byte[], FileWriteOptions)"/> or
        /// <see cref="FileEntry.GetWriteStream(FileWriteOptions)"/> to materialise it, or <see cref="Exists"/>
        /// to test whether it already exists.
        /// </summary>
        /// <remarks>
        /// This is the only way to construct a <see cref="LocalFile"/> outside of
        /// the driver — it always anchors the file to a <see cref="LocalDirectory"/>
        /// so the hub's sandbox root travels with the reference via the parent
        /// chain. Raw disk paths are deliberately not accepted.
        /// </remarks>
        public LocalFile(LocalDirectory directory, string fileName) : base(fileName)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            PathUtil.ValidateLocalName(fileName);
            Parent = directory;
        }

        public override bool Exists() => File.Exists(Path);

        public override void Delete()
        {
            ThrowIfReadOnly();
            try
            {
                File.Delete(Path);
            }
            catch (DirectoryNotFoundException)
            {
                // Parent directory gone — nothing to delete. Deletion is idempotent.
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions to callers.
                throw new FileHubException($"Failed to delete \"{Path}\".", ex);
            }
        }

        // 80 KB matches the copy-loop buffer in FileEntry, so each ReadAsync
        // maps to a single overlapped I/O request.
        private const int StreamBufferSize = 81920;

        public override Stream GetReadStream()
        {
            // FileOptions.Asynchronous binds the handle to overlapped I/O so
            // ReadAsync is truly asynchronous instead of a sync read queued on
            // a thread-pool thread — without it, concurrent copies pin one
            // pool thread each and starve the pool under load.
            return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        // StreamPreference is ignored: disk writes stream straight to the file, so
        // there is no single-request vs multipart distinction to honor.
        public override Stream GetWriteStream(FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            return new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None,
                StreamBufferSize, FileOptions.Asynchronous);
        }

        public override FileEntry Rename(string newName)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory under the current parent
            // and move into it (never overwriting, like any rename).
            if (NestedPath.HasSeparator(newName))
            {
                if (NestedPath.TrySplitLeaf(newName, out var subPath, out var leaf))
                    return MoveTo(Parent.CreateDirectory(subPath), leaf, progress: null, overwrite: false);
                newName = leaf;
            }

            PathUtil.ValidateLocalName(newName);
            var newPath = ((LocalDirectory)Parent).ResolveSafeChildPath(newName);

            // Rename never overwrites — a name already taken is an error.
            if (File.Exists(newPath) || Directory.Exists(newPath))
                throw new FileAlreadyExistsException(newPath);

            try
            {
                File.Move(Path, newPath);
            }
            catch (IOException ex)
            {
                // Never leak raw System.IO exceptions. A target that appeared
                // between the check and the move surfaces as the library's
                // conflict exception; anything else as a generic FileHubException.
                if (File.Exists(newPath) || Directory.Exists(newPath))
                    throw new FileAlreadyExistsException(newPath);
                throw new FileHubException($"Failed to rename \"{Path}\" to \"{newName}\".", ex);
            }

            Name = newName;
            _info = null;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
        {
            ThrowIfReadOnly();

            // Same-filesystem move: rename via File.Move — atomic within a volume,
            // and .NET's File.Move already handles the cross-volume case by
            // copying then deleting the source. Only a cross-driver destination
            // (e.g. moving into an S3 hub) needs the manual copy + delete below.
            if (directory is LocalDirectory localDir)
            {
                if (NestedPath.HasSeparator(name))
                {
                    if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                        return MoveTo(localDir.CreateDirectory(subPath), leaf, progress, overwrite);
                    name = leaf;
                }

                PathUtil.ValidateLocalName(name);
                var destPath = localDir.ResolveSafeChildPath(name);

                // Moving onto the same physical path is a caller error — refuse
                // it explicitly. Silently succeeding would hide the mistake, and
                // "clearing the target" below would delete the source before the
                // File.Move, losing the file.
                if (string.Equals(System.IO.Path.GetFullPath(Path), destPath, StringComparison.OrdinalIgnoreCase))
                    throw new FileAlreadyExistsException($"Cannot move \"{Path}\" onto itself.", destPath);

                // Native File.Move is atomic and fast but reports no byte-level
                // progress. With a progress sink, fall through to the streaming
                // copy+delete (MoveAcrossDrivers) so callers still see granular
                // progress.
                if (progress == null)
                {
                    if (File.Exists(destPath) || Directory.Exists(destPath))
                    {
                        if (!overwrite)
                            throw new FileAlreadyExistsException(destPath);
                        // A file must never replace a directory — mirror
                        // System.IO.File.Move, which refuses this regardless of
                        // overwrite rather than recursively wiping the folder.
                        if (Directory.Exists(destPath))
                            throw new FileHubException($"Cannot overwrite directory \"{destPath}\" with the file \"{Path}\".");
                    }

                    try
                    {
                        // File.Move has no overwrite overload on netstandard2.0,
                        // so clear the existing target first.
                        if (File.Exists(destPath))
                            File.Delete(destPath);
                        File.Move(Path, destPath);
                    }
                    catch (IOException ex)
                    {
                        if (File.Exists(destPath) || Directory.Exists(destPath))
                            throw new FileAlreadyExistsException(destPath);
                        throw new FileHubException($"Failed to move \"{Path}\" to \"{destPath}\".", ex);
                    }

                    return new LocalFile(localDir, name);
                }
            }

            return MoveAcrossDrivers(directory, name, progress, overwrite);
        }

        // Cross-driver move: copy the bytes, then delete the source. The copy
        // runs first so a failure leaves the original intact; a delete that
        // fails after a good copy surfaces as PartialMoveException instead of
        // pretending the move fully succeeded or fully failed.
        private FileEntry MoveAcrossDrivers(FileDirectory directory, string name, IProgress<TransferStatus> progress, bool overwrite)
        {
            var newFile = CopyTo(directory, name, progress, overwrite);
            try
            {
                Delete();
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

        public override Task<FileEntry> MoveToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();

            // A same-filesystem move is a fast local syscall — run it inline.
            // A cross-driver move must stay genuinely async (it may hit the
            // network), so defer to the async copy + delete below.
            if (directory is LocalDirectory)
                return Task.FromResult(MoveTo(directory, name, progress, overwrite));

            return MoveAcrossDriversAsync(directory, name, progress, overwrite, cancellationToken);
        }

        private async Task<FileEntry> MoveAcrossDriversAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress, bool overwrite, CancellationToken cancellationToken)
        {
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

        // Same-filesystem copy uses File.Copy so the OS moves the bytes; a
        // cross-driver destination falls back to the stream-based base copy.
        public override FileEntry CopyTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
        {
            if (directory is LocalDirectory localDir)
            {
                if (NestedPath.HasSeparator(name))
                {
                    if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                        return CopyTo(localDir.CreateDirectory(subPath), leaf, progress, overwrite);
                    name = leaf;
                }

                PathUtil.ValidateLocalName(name);
                var destPath = localDir.ResolveSafeChildPath(name);

                // Copying onto the same physical path is a caller error — refuse
                // it explicitly rather than lean on File.Copy's opaque IOException.
                if (string.Equals(System.IO.Path.GetFullPath(Path), destPath, StringComparison.OrdinalIgnoreCase))
                    throw new FileAlreadyExistsException($"Cannot copy \"{Path}\" onto itself.", destPath);

                if (!overwrite && (File.Exists(destPath) || Directory.Exists(destPath)))
                    throw new FileAlreadyExistsException(destPath);

                // Native File.Copy lets the OS move the bytes but reports no
                // byte-level progress. With a progress sink, fall through to the
                // stream-based base copy so callers still see granular progress.
                if (progress == null)
                {
                    try
                    {
                        File.Copy(Path, destPath, overwrite);
                    }
                    catch (IOException ex)
                    {
                        if (!overwrite && (File.Exists(destPath) || Directory.Exists(destPath)))
                            throw new FileAlreadyExistsException(destPath);
                        throw new FileHubException($"Failed to copy \"{Path}\" to \"{destPath}\".", ex);
                    }

                    return new LocalFile(localDir, name);
                }
            }

            return base.CopyTo(directory, name, progress, overwrite);
        }

        private FileInfo RefreshInfo()
        {
            if (_info == null || !_info.FullName.Equals(Path, StringComparison.OrdinalIgnoreCase))
                _info = new FileInfo(Path);

            _info.Refresh();
            return _info;
        }
    }
}
