using System;
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

        // Portable rule set (see PathUtil): a name accepted here is accepted
        // by every driver on every OS. OS-backed drivers layer
        // PathUtil.ValidateLocalName on top.
        protected static void ValidateName(string name) => PathUtil.ValidateName(name);

        public virtual void Dispose()
        {
            Disposed = true;
        }
    }
}
