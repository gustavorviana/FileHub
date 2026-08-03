using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Memory
{
    public class MemoryFile : FileEntry
    {
        internal MemoryFileData Data { get; }
        private readonly MemoryDirectory _parent;

        // Driver-neutral "/" separator — see MemoryDirectory's Path note.
        public override string Path => _parent != null
            ? PathUtil.JoinDisplay(_parent.Path, Name)
            : Name;

        public override FileDirectory Parent => _parent;
        public override long Length => Data.Stream.Length;
        public override DateTime CreationTimeUtc => Data.CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => Data.LastWriteTimeUtc;

        internal MemoryFile(MemoryDirectory parent, MemoryFileData data) : base(data.Name)
        {
            _parent = parent;
            Data = data;
        }

        public override bool Exists() => !Disposed && _parent != null && _parent.ContainsFile(Name);

        public override void Delete()
        {
            ThrowIfReadOnly();
            _parent?.RemoveFile(Name);
        }

        public override Stream GetReadStream()
        {
            Data.AcquireRead();
            try
            {
                Data.Stream.Position = 0;
                return new NonDisposableMemoryStream(Data, isWriter: false);
            }
            catch
            {
                Data.ReleaseRead();
                throw;
            }
        }

        // StreamPreference is ignored: the payload lives in process memory either
        // way, so there is no single-request vs multipart distinction.
        public override Stream GetWriteStream(FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            Data.ApplyOptions(options);
            Data.AcquireWrite();
            try
            {
                Data.Stream.SetLength(0);
                Data.Stream.Position = 0;
                return new NonDisposableMemoryStream(Data, isWriter: true);
            }
            catch
            {
                Data.ReleaseWrite();
                throw;
            }
        }

        public override FileEntry Rename(string newName)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a
            // path — resolve/create that subdirectory and move into it.
            if (NestedPath.HasSeparator(newName))
            {
                if (NestedPath.TrySplitLeaf(newName, out var subPath, out var leaf))
                    return MoveTo(Parent.CreateDirectory(subPath), leaf, progress: null, overwrite: false);
                newName = leaf;
            }

            ValidateName(newName);

            // Rename never overwrites — a name already taken is an error.
            if (_parent != null && _parent.Exists(newName))
                throw new FileAlreadyExistsException(PathUtil.JoinDisplay(_parent.Path, newName));

            _parent?.RemoveFile(Name);
            Name = newName;
            Data.Name = newName;
            _parent?.AddFile(Data);
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
        {
            ThrowIfReadOnly();

            // A separator means the tail is the real name and the rest is a path —
            // resolve/create that subdirectory and recurse with the leaf.
            if (NestedPath.HasSeparator(name))
            {
                if (NestedPath.TrySplitLeaf(name, out var subPath, out var leaf))
                    return MoveTo(directory.CreateDirectory(subPath), leaf, progress, overwrite);
                name = leaf;
            }

            // Same-store move: re-link the backing data instead of cloning the
            // bytes. The payload is a process object, so handing over the
            // reference is O(1) regardless of file size (works across Memory hubs).
            if (directory is MemoryDirectory memDir)
            {
                ValidateName(name);
                // Same directory instance + same name means moving onto itself.
                if (ReferenceEquals(memDir, _parent) && string.Equals(name, Name, StringComparison.OrdinalIgnoreCase))
                    throw new FileAlreadyExistsException($"Cannot move \"{Path}\" onto itself.", Path);
                if (!overwrite && memDir.Exists(name))
                    throw new FileAlreadyExistsException(PathUtil.JoinDisplay(memDir.Path, name));

                var length = Length;
                _parent?.RemoveFile(Name);
                Data.Name = name;
                memDir.AddFile(Data);
                progress?.Report(new TransferStatus(length, length));
                return new MemoryFile(memDir, Data);
            }

            // Cross-driver: copy the bytes, then delete the source, guarding the
            // delete so a post-copy failure surfaces as a partial move.
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
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MoveTo(directory, name, progress, overwrite));
        }

        public override FileEntry CopyTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true)
        {
            ThrowIfCopyOntoSelf(directory, name);
            return base.CopyTo(directory, name, progress, overwrite);
        }

        public override Task<FileEntry> CopyToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true, CancellationToken cancellationToken = default)
        {
            ThrowIfCopyOntoSelf(directory, name);
            return base.CopyToAsync(directory, name, progress, overwrite, cancellationToken);
        }

        // Same directory instance + same leaf name resolves to this very file —
        // a copy onto itself. A separator means a nested target, never self.
        private void ThrowIfCopyOntoSelf(FileDirectory directory, string name)
        {
            if (directory is MemoryDirectory memDir
                && ReferenceEquals(memDir, _parent)
                && !NestedPath.HasSeparator(name)
                && string.Equals(name, Name, StringComparison.OrdinalIgnoreCase))
                throw new FileAlreadyExistsException($"Cannot copy \"{Path}\" onto itself.", Path);
        }

        // === FileWriteOptions / metadata support ===

        public override Task<FileMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FileMetadata(
                contentType: Data.ContentType,
                cacheControl: Data.CacheControl,
                tags: Data.Metadata));
        }
    }
}
