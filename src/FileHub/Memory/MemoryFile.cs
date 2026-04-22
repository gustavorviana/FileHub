using System;
using System.IO;

namespace FileHub.Memory
{
    public class MemoryFile : FileEntry
    {
        internal MemoryFileData Data { get; }
        private readonly MemoryDirectory _parent;

        public override string Path => _parent != null
            ? System.IO.Path.Combine(_parent.Path, Name)
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

        public override Stream GetWriteStream()
        {
            ThrowIfReadOnly();
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
            ValidateName(newName);
            _parent?.RemoveFile(Name);
            Name = newName;
            Data.Name = newName;
            _parent?.AddFile(Data);
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();
            var newFile = CopyTo(directory, name);
            Delete();
            return newFile;
        }
    }
}
