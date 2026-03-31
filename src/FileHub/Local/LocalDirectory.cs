using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileHub.Local
{
    public class LocalDirectory : FileDirectory
    {
        public override string Path { get; }
        public override FileDirectory Parent { get; }

        public override DateTime CreationTimeUtc => new DirectoryInfo(Path).CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => new DirectoryInfo(Path).LastWriteTimeUtc;

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
            return dir.GetFiles(searchPattern, SearchOption.TopDirectoryOnly)
                .Select(f => new LocalFile(this, f.Name, RootPath));
        }

        // === Directory operations ===

        public override FileDirectory CreateDirectory(string name)
        {
            ThrowIfReadOnly();
            ValidateName(name);
            var dirPath = ResolveSafePath(name);
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
            return dir.GetDirectories(searchPattern, SearchOption.TopDirectoryOnly)
                .Select(d => new LocalDirectory(d.FullName, RootPath, this));
        }

        // === Common operations ===

        public override bool ItemExists(string name)
        {
            var fullPath = System.IO.Path.Combine(Path, name);
            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }

        public override bool Exists() => Directory.Exists(Path);

        public override void Delete()
        {
            ThrowIfReadOnly();
            Directory.Delete(Path, recursive: true);
        }

        public override void Delete(string name)
        {
            ThrowIfReadOnly();
            var fullPath = ResolveSafePath(name);
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
            else if (File.Exists(fullPath))
                File.Delete(fullPath);
            else
                throw new FileNotFoundException($"The item \"{name}\" was not found in \"{Path}\".");
        }

        public override FileDirectory Rename(string newName)
        {
            ThrowIfReadOnly();
            ValidateName(newName);
            var parentPath = System.IO.Path.GetDirectoryName(Path);
            var newPath = System.IO.Path.Combine(parentPath, newName);
            Directory.Move(Path, newPath);
            return new LocalDirectory(newPath, RootPath, Parent);
        }

        public override FileDirectory MoveTo(FileDirectory directory, string name)
        {
            ThrowIfReadOnly();
            var copied = CopyTo(directory, name);
            Directory.Delete(Path, recursive: true);
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
        }

        // === Helpers ===

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
