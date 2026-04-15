using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace FileHub
{
    public abstract class FileDirectory : FileSystemEntry
    {
        public abstract FileDirectory Parent { get; }
        protected string RootPath { get; }

        protected FileDirectory(string name, string rootPath) : base(name)
        {
            RootPath = rootPath;
        }

        // === Abstract (drivers implement) ===

        public abstract FileEntry CreateFile(string name);
        public abstract bool TryOpenFile(string name, out FileEntry file);
        public abstract IEnumerable<FileEntry> GetFiles(string searchPattern = "*");

        public abstract FileDirectory CreateDirectory(string name);
        public abstract bool TryOpenDirectory(string name, out FileDirectory directory);
        public abstract IEnumerable<FileDirectory> GetDirectories(string searchPattern = "*");

        public abstract bool ItemExists(string name);
        public abstract void Delete();
        public abstract void Delete(string name);
        public abstract FileDirectory Rename(string newName);
        public abstract FileDirectory MoveTo(FileDirectory directory, string name);
        public abstract FileDirectory CopyTo(FileDirectory directory, string name);
        public abstract void SetLastWriteTime(DateTime date);

        // === Sync default implementations ===

        public FileEntry CreateFile(string name, bool overwrite)
        {
            ThrowIfReadOnly();
            if (overwrite) DeleteIfExists(name);
            return CreateFile(name);
        }

        public FileEntry OpenFile(string name)
        {
            return OpenFile(name, createIfNotExists: false);
        }

        public FileEntry OpenFile(string name, bool createIfNotExists)
        {
            if (TryOpenFile(name, out var file))
                return file;

            if (createIfNotExists)
                return CreateFile(name);

            throw new FileNotFoundException($"The file \"{System.IO.Path.Combine(Path, name)}\" was not found.");
        }

        public FileDirectory OpenDirectory(string name)
        {
            return OpenDirectory(name, createIfNotExists: false);
        }

        public FileDirectory OpenDirectory(string name, bool createIfNotExists)
        {
            if (TryOpenDirectory(name, out var directory))
                return directory;

            if (createIfNotExists)
                return CreateDirectory(name);

            throw new DirectoryNotFoundException($"The directory \"{System.IO.Path.Combine(Path, name)}\" was not found.");
        }

        public void DeleteIfExists(string name)
        {
            ThrowIfReadOnly();
            if (ItemExists(name))
                Delete(name);
        }

        // === Async defaults (wrap sync - cloud drivers override) ===

        public virtual Task<FileEntry> CreateFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFile(name));
        }

        public virtual Task<FileEntry> CreateFileAsync(string name, bool overwrite, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateFile(name, overwrite));
        }

        public virtual Task<FileEntry> OpenFileAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name));
        }

        public virtual Task<FileEntry> OpenFileAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenFile(name, createIfNotExists));
        }

        public virtual Task<FileDirectory> CreateDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDirectory(name));
        }

        public virtual Task<FileDirectory> OpenDirectoryAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenDirectory(name));
        }

        public virtual Task<FileDirectory> OpenDirectoryAsync(string name, bool createIfNotExists, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenDirectory(name, createIfNotExists));
        }

        public virtual Task<bool> ItemExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ItemExists(name));
        }

        public virtual Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete();
            return Task.CompletedTask;
        }

        public virtual Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete(name);
            return Task.CompletedTask;
        }

        public virtual Task DeleteIfExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(name);
            return Task.CompletedTask;
        }

        public virtual Task SetLastWriteTimeAsync(DateTime date, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetLastWriteTime(date);
            return Task.CompletedTask;
        }

        public virtual Task<FileDirectory> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Rename(newName));
        }

        public virtual Task<FileDirectory> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MoveTo(directory, name));
        }

        public virtual Task<FileDirectory> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CopyTo(directory, name));
        }

#if NET8_0_OR_GREATER
        public virtual async IAsyncEnumerable<FileEntry> GetFilesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var file in GetFiles(searchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public virtual async IAsyncEnumerable<FileDirectory> GetDirectoriesAsync(
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var dir in GetDirectories(searchPattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return dir;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }
#else
        public virtual Task<IEnumerable<FileEntry>> GetFilesAsync(string searchPattern = "*", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetFiles(searchPattern));
        }

        public virtual Task<IEnumerable<FileDirectory>> GetDirectoriesAsync(string searchPattern = "*", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetDirectories(searchPattern));
        }
#endif

        // === Helpers ===

        protected string ResolveSafePath(string relativePath)
        {
            if (string.IsNullOrEmpty(RootPath))
                return relativePath;

            var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
            EnsureWithinRoot(fullPath);
#if NET8_0_OR_GREATER
            EnsureNoSymlinkEscape(fullPath);
#endif
            return fullPath;
        }

        protected void EnsureWithinRoot(string fullPath)
        {
            if (string.IsNullOrEmpty(RootPath)) return;

            var normalizedRoot = System.IO.Path.GetFullPath(RootPath);
            var separator = System.IO.Path.DirectorySeparatorChar;
            var rootWithSep = normalizedRoot.EndsWith(separator.ToString())
                ? normalizedRoot
                : normalizedRoot + separator;

            bool isRoot = string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
            bool isInside = fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

            if (!isRoot && !isInside)
                throw new FileHubException($"Access denied: path \"{fullPath}\" is outside the root directory.");
        }

#if NET8_0_OR_GREATER
        protected void EnsureNoSymlinkEscape(string fullPath)
        {
            if (string.IsNullOrEmpty(RootPath)) return;

            FileSystemInfo info = null;
            if (System.IO.File.Exists(fullPath))
                info = new System.IO.FileInfo(fullPath);
            else if (System.IO.Directory.Exists(fullPath))
                info = new System.IO.DirectoryInfo(fullPath);

            if (info?.LinkTarget == null) return;

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved == null) return;

            EnsureWithinRoot(resolved.FullName);
        }
#endif

        protected string FixPath(string path)
        {
            var separator = System.IO.Path.DirectorySeparatorChar;
            var pBuilder = new StringBuilder(path.Length);
            int separators = 0;
            foreach (char c in path)
            {
                if (c == '/' || c == '\\') separators++;
                else separators = 0;

                if (separators == 0) pBuilder.Append(c);
                else if (separators == 1) pBuilder.Append(separator);
            }

            return pBuilder.ToString();
        }

        public override string ToString() => Path;
    }
}
