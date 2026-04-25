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

        /// <summary>
        /// Create a <see cref="LocalFile"/> reference pointing at <paramref name="fileName"/>
        /// inside <paramref name="directory"/>. The file itself is not created on
        /// disk — call <see cref="FileEntry.SetText"/>, <see cref="FileEntry.SetBytes"/> or
        /// <see cref="GetWriteStream"/> to materialise it, or <see cref="Exists"/>
        /// to test whether it already exists.
        /// </summary>
        /// <remarks>
        /// This is the only way to construct a <see cref="LocalFile"/> outside of
        /// the driver — it always anchors the file to a <see cref="LocalDirectory"/>
        /// so the hub's sandbox root travels with the reference. Raw disk paths
        /// are deliberately not accepted.
        /// </remarks>
        public LocalFile(LocalDirectory directory, string fileName) : base(fileName)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            ValidateName(fileName);
            Parent = directory;
            _rootPath = directory.RootPathInternal;
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
            var newPath = ((LocalDirectory)Parent).ResolveSafeChildPath(newName);
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

        private FileInfo RefreshInfo()
        {
            if (_info == null || !_info.FullName.Equals(Path, StringComparison.OrdinalIgnoreCase))
                _info = new FileInfo(Path);

            _info.Refresh();
            return _info;
        }
    }
}
