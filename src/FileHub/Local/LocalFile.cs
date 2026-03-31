using System;
using System.IO;

namespace FileHub.Local
{
    public class LocalFile : FileEntry
    {
        private readonly string _rootPath;
        private FileInfo _info;

        public override string Path => System.IO.Path.Combine(Parent.Path, Name);
        public override FileDirectory Parent { get; }
        public override long Length => RefreshInfo().Length;
        public override DateTime CreationTimeUtc => RefreshInfo().CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => RefreshInfo().LastWriteTimeUtc;

        internal LocalFile(FileDirectory parent, string name, string rootPath) : base(name)
        {
            Parent = parent;
            _rootPath = rootPath;
        }

        public override bool Exists() => File.Exists(Path);

        public override void Delete()
        {
            ThrowIfReadOnly();
            File.Delete(Path);
        }

        public override Stream GetReadStream()
        {
            return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public override Stream GetWriteStream()
        {
            ThrowIfReadOnly();
            return new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None);
        }

        public override FileEntry Rename(string newName)
        {
            ThrowIfReadOnly();
            ValidateName(newName);
            var newPath = System.IO.Path.Combine(Parent.Path, newName);
            File.Move(Path, newPath);
            Name = newName;
            _info = null;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();
            var newFile = CopyTo(directory, name);
            Delete();
            return newFile;
        }

        public override void SetLastWriteTime(DateTime date)
        {
            ThrowIfReadOnly();
            File.SetLastWriteTimeUtc(Path, date);
            _info = null;
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
