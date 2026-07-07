using System;
using System.IO;

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
            File.Delete(Path);
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

        public override Stream GetWriteStream(FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            return new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None,
                StreamBufferSize, FileOptions.Asynchronous);
        }

        public override FileEntry Rename(string newName)
        {
            ThrowIfReadOnly();
            PathUtil.ValidateLocalName(newName);
            var newPath = ((LocalDirectory)Parent).ResolveSafeChildPath(newName);
            File.Move(Path, newPath);
            Name = newName;
            _info = null;
            return this;
        }

        public override FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null)
        {
            ThrowIfReadOnly();
            var newFile = CopyTo(directory, name, progress);
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
