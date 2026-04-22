using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileHub
{
    public static class FileHubExtensions
    {
        public static FileEntry AsReadOnly(this FileEntry file)
        {
            if (file.IsReadOnly) return file;
            return new ReadOnlyFileWrapper(file);
        }

        public static FileDirectory AsReadOnly(this FileDirectory directory)
        {
            if (directory.IsReadOnly) return directory;
            return new ReadOnlyDirectoryWrapper(directory);
        }
    }

    internal class ReadOnlyFileWrapper : FileEntry
    {
        private readonly FileEntry _inner;
        private FileDirectory _parentWrapped;

        public override string Path => _inner.Path;
        public override FileDirectory Parent =>
            _parentWrapped ??= _inner.Parent != null ? new ReadOnlyDirectoryWrapper(_inner.Parent) : null;
        public override long Length => _inner.Length;
        public override DateTime CreationTimeUtc => _inner.CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => _inner.LastWriteTimeUtc;

        internal ReadOnlyFileWrapper(FileEntry inner) : base(inner.Name)
        {
            _inner = inner;
            IsReadOnly = true;
        }

        public override bool Exists() => _inner.Exists();
        public override Stream GetReadStream() => _inner.GetReadStream();

        public override Stream GetWriteStream() { ThrowIfReadOnly(); return null; }
        public override void Delete() => ThrowIfReadOnly();
        public override FileEntry Rename(string newName) { ThrowIfReadOnly(); return null; }
        public override FileEntry MoveTo(FileDirectory directory, string name) { ThrowIfReadOnly(); return null; }
    }

    internal class ReadOnlyDirectoryWrapper : FileDirectory
    {
        private readonly FileDirectory _inner;
        private FileDirectory _parentWrapped;

        public override string Path => _inner.Path;
        public override FileDirectory Parent =>
            _parentWrapped ??= _inner.Parent != null ? new ReadOnlyDirectoryWrapper(_inner.Parent) : null;
        public override DateTime CreationTimeUtc => _inner.CreationTimeUtc;
        public override DateTime LastWriteTimeUtc => _inner.LastWriteTimeUtc;

        internal ReadOnlyDirectoryWrapper(FileDirectory inner) : base(inner.Name, rootPath: null)
        {
            _inner = inner;
            IsReadOnly = true;
        }

        public override bool Exists() => _inner.Exists();
        public override bool ItemExists(string name) => _inner.ItemExists(name);

        public override bool TryOpenFile(string name, out FileEntry file)
        {
            if (_inner.TryOpenFile(name, out var f))
            {
                file = f.AsReadOnly();
                return true;
            }
            file = null;
            return false;
        }

        public override IEnumerable<FileEntry> GetFiles(string searchPattern = "*", FileListOffset offset = default, int? limit = null)
        {
            return _inner.GetFiles(searchPattern, offset, limit).Select(f => f.AsReadOnly());
        }

        public override bool TryOpenDirectory(string name, out FileDirectory directory)
        {
            if (_inner.TryOpenDirectory(name, out var d))
            {
                directory = d.AsReadOnly();
                return true;
            }
            directory = null;
            return false;
        }

        public override IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*")
        {
            return _inner.GetDirectories(searchPattern).Select(d => d.AsReadOnly());
        }

        // Write operations - all throw
        public override FileEntry CreateFile(string name) { ThrowIfReadOnly(); return null; }
        public override FileDirectory CreateDirectory(string name) { ThrowIfReadOnly(); return null; }
        public override void Delete() => ThrowIfReadOnly();
        public override void Delete(string name) => ThrowIfReadOnly();
        public override FileDirectory Rename(string newName) { ThrowIfReadOnly(); return null; }
        public override FileDirectory MoveTo(FileDirectory directory, string name) { ThrowIfReadOnly(); return null; }
        public override FileDirectory CopyTo(FileDirectory directory, string name) { ThrowIfReadOnly(); return null; }
    }
}
