using System;
using System.Collections.Generic;
using System.IO;

namespace FileHub.Local
{
    public class LocalDirectory : FileDirectory
    {
        private DirectoryInfo _info;

        public override string Path { get; }
        public override FileDirectory Parent { get; }

        public override DateTime CreationTimeUtc => RefreshInfo().CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => RefreshInfo().LastWriteTimeUtc;

        internal LocalDirectory(string path, string rootPath, FileDirectory parent)
            : base(GetDirectoryName(path), rootPath)
        {
            Path = path;
            Parent = parent;

            if (!Directory.Exists(Path))
                Directory.CreateDirectory(Path);
        }

        // === File operations ===

        public override FileEntry CreateFile(string name)
        {
            ThrowIfReadOnly();
            ValidateName(name);
            var filePath = ResolveSafePath(name);
            File.Create(filePath).Dispose();
            InvalidateInfo();
            return new LocalFile(this, name, RootPath);
        }

        public override bool TryOpenFile(string name, out FileEntry file)
        {
            ValidateName(name);
            file = null;

            var filePath = ResolveSafePath(name);
            if (!File.Exists(filePath))
                return false;

            file = new LocalFile(this, name, RootPath);
            return true;
        }

        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*")
        {
            var dir = new DirectoryInfo(Path);
            foreach (var f in dir.GetFiles(searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (ShouldSkipLink(f)) continue;
                yield return new LocalFile(this, f.Name, RootPath);
            }
        }

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name)
        {
            ThrowIfReadOnly();
            ValidateName(name);
            var dirPath = ResolveSafePath(name);
            InvalidateInfo();
            return new LocalDirectory(dirPath, RootPath, this);
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            ValidateName(name);
            directory = null;

            var dirPath = ResolveSafePath(name);
            if (!Directory.Exists(dirPath))
                return false;

            directory = new LocalDirectory(dirPath, RootPath, this);
            return true;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            var dir = new DirectoryInfo(Path);
            foreach (var d in dir.GetDirectories(searchPattern, SearchOption.TopDirectoryOnly))
            {
                if (ShouldSkipLink(d)) continue;
                yield return new LocalDirectory(d.FullName, RootPath, this);
            }
        }

        // === Common operations ===

        public override bool ItemExists(string name)
        {
            ValidateName(name);
            var fullPath = ResolveSafePath(name);
            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }

        public override bool Exists() => Directory.Exists(Path);

        public override void Delete()
        {
            ThrowIfReadOnly();
            Directory.Delete(Path, recursive: true);
            InvalidateInfo();
        }

        public override void Delete(string name)
        {
            ThrowIfReadOnly();
            ValidateName(name);
            var fullPath = ResolveSafePath(name);
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
            else if (File.Exists(fullPath))
                File.Delete(fullPath);
            else
                throw new FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
            InvalidateInfo();
        }

        public override FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();
            ValidateName(newName);

            var parentPath = System.IO.Path.GetDirectoryName(Path);
            var newPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentPath, newName));

            EnsureWithinRoot(newPath);

            Directory.Move(Path, newPath);
            return new LocalDirectory(newPath, RootPath, Parent);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();

            var copied = CopyTo(directory, name);
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Rollback: delete the copy that was made to keep state consistent
                try { copied.Delete(); } catch { }
                throw;
            }
            return copied;
        }

        public override FileDirectory CopyTo(FileDirectory directory, string name)
        {
            var newDir = directory.CreateDirectory(name);
            CopyContents(this, newDir);
            return newDir;
        }

        public override void SetLastWriteTime(DateTime date)
        {
            ThrowIfReadOnly();
            Directory.SetLastWriteTimeUtc(Path, date);
            InvalidateInfo();
        }

        // === Helpers ===

        private DirectoryInfo RefreshInfo()
        {
            if (_info == null)
                _info = new DirectoryInfo(Path);
            _info.Refresh();
            return _info;
        }

        private void InvalidateInfo()
        {
            _info = null;
        }

        private static bool ShouldSkipLink(FileSystemInfo info)
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }

        private static void CopyContents(FileDirectory source, FileDirectory destination)
        {
            foreach (var file in source.GetFiles())
                file.CopyTo(destination, file.Name);

            foreach (var subDir in source.GetDirectories())
            {
                var newSubDir = destination.CreateDirectory(subDir.Name);
                CopyContents(subDir, newSubDir);
            }
        }

        private static string GetDirectoryName(string path)
        {
            path = path.TrimEnd('/', '\\');
            int index = path.LastIndexOfAny(new[] { '/', '\\' });
            return index == -1 ? path : path.Substring(index + 1);
        }
    }
}
