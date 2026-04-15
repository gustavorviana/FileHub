using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    public abstract class FileSystemEntry : IDisposable
    {
        public abstract string Path { get; }
        public string Name { get; protected set; }
        public bool IsReadOnly { get; protected set; }
        public abstract DateTime CreationTimeUtc { get; }
        public abstract DateTime LastWriteTimeUtc { get; }

        protected bool Disposed { get; private set; }

        protected FileSystemEntry(string name)
        {
            Name = name;
        }

        public abstract bool Exists();

        public virtual Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Exists());
        }

        protected void ThrowIfReadOnly()
        {
            if (IsReadOnly)
                throw new FileHubException("This item is read-only.");
        }

        protected static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            if (name.Any(c => invalidChars.Contains(c)))
                throw new ArgumentException($"The name \"{name}\" contains invalid characters.", nameof(name));
        }

        public virtual void Dispose()
        {
            Disposed = true;
        }
    }
}
