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

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = true)
        {
            ThrowIfReadOnly();
            var newFile = CopyTo(directory, name, progress, overwrite);
            Delete();
            return newFile;
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
