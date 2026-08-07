using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    public abstract class FileEntry : FileSystemEntry
    {
        /// <summary>Read buffer size.</summary>
        internal const int ReadBufferSize = 1024 * 1024; // 1 MiB

        public virtual string Extension => System.IO.Path.GetExtension(Name);
        public abstract long Length { get; }
        public abstract DirectoryEntry Parent { get; }

        protected FileEntry(string name) : base(name) { }

        // === Sync abstract (drivers implement) ===

        public abstract Stream GetReadStream();

        /// <summary>
        /// Opens a write stream. <see cref="FileWriteOptions.StreamPreference"/>
        /// hints how the stream should commit; drivers without multipart
        /// support ignore it silently.
        /// </summary>
        public abstract Stream GetWriteStream(FileWriteOptions options = null);

        /// <summary>
        /// Deletes the file. Idempotent-silent: deleting a file that no longer
        /// exists is a no-op on every backend, matching <c>File.Delete</c>.
        /// </summary>
        public abstract void Delete();

        /// <summary>
        /// Change this file's leaf name in place under the same directory.
        /// <paramref name="newName"/> must be a single name: a value containing
        /// a <c>/</c> or <c>\</c> separator throws
        /// <see cref="System.ArgumentException"/> — use
        /// <see cref="MoveTo(DirectoryEntry, string, IProgress{TransferStatus}, bool)"/>
        /// to relocate. An existing target throws
        /// <see cref="FileAlreadyExistsException"/>.
        /// </summary>
        public abstract FileEntry Rename(string newName);
        public abstract FileEntry MoveTo(DirectoryEntry directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false);

        // === Sync convenience (implemented using streams) ===

        public string ReadAllText()
        {
            return ReadAllText(Encoding.UTF8);
        }

        public string ReadAllText(Encoding encoding)
        {
            using var stream = GetReadStream();
            using var reader = new StreamReader(stream, encoding);
            return reader.ReadToEnd();
        }

        public byte[] ReadAllBytes()
        {
            using var ms = new MemoryStream();
            using var stream = GetReadStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public virtual void SetText(string content, Encoding encoding = null, FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            var bytes = (encoding ?? Encoding.UTF8).GetBytes(content);
            using var stream = GetWriteStream(options);
            stream.Write(bytes, 0, bytes.Length);
        }

        public virtual void SetBytes(byte[] buffer, FileWriteOptions options = null)
        {
            ThrowIfReadOnly();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            using var stream = GetWriteStream(options);
            stream.Write(buffer, 0, buffer.Length);
        }

        public void CopyToStream(Stream destination, IProgress<TransferStatus> progress = null)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new NotSupportedException("The destination stream does not support writing.");

            var buffer = new byte[ReadBufferSize];
            var total = progress != null ? Length : 0;
            long transferred = 0;
            int bytesRead;
            using var source = GetReadStream();
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, bytesRead);
                if (progress != null)
                {
                    transferred += bytesRead;
                    progress.Report(new TransferStatus(transferred, total));
                }
            }
        }

        public void CopyFromStream(Stream source, FileWriteOptions options = null, IProgress<TransferStatus> progress = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.CanRead) throw new NotSupportedException("The source stream does not support reading.");

            ThrowIfReadOnly();
            var buffer = new byte[ReadBufferSize];
            var total = progress != null ? source.Length : 0;
            long transferred = 0;
            int bytesRead;

            using var destination = GetWriteStream(options);

            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, bytesRead);
                if (progress != null)
                {
                    transferred += bytesRead;
                    progress.Report(new TransferStatus(transferred, total));
                }
            }
        }

        public virtual FileEntry CopyTo(string newName, IProgress<TransferStatus> progress = null, bool overwrite = false)
            => CopyTo(Parent, newName, progress, overwrite);

        public virtual FileEntry CopyTo(DirectoryEntry directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false)
        {
            if (!overwrite && directory.Exists(name))
                throw new FileAlreadyExistsException(directory.CombineChildPath(name));

            var newFile = directory.CreateFile(name, overwrite);
            using (var writeStream = newFile.GetWriteStream())
                CopyToStream(writeStream, progress);
            return newFile;
        }

        // === Async defaults (wrap sync - cloud drivers override) ===

        public virtual Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetReadStream());
        }

        /// <summary>Async version of <see cref="GetWriteStream(FileWriteOptions)"/>.</summary>
        public virtual Task<Stream> GetWriteStreamAsync(FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetWriteStream(options));
        }

        public virtual async Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
        {
            return await ReadAllTextAsync(Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = await GetReadStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, encoding);

            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        public virtual async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = await GetReadStreamAsync(cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream();

            await stream.CopyToAsync(ms, ReadBufferSize, cancellationToken).ConfigureAwait(false);
            return ms.ToArray();
        }

        public virtual async Task SetTextAsync(string content, Encoding encoding = null, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = (encoding ?? Encoding.UTF8).GetBytes(content);

            var stream = await GetWriteStreamAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
#if NET8_0_OR_GREATER
                await stream.DisposeAsync().ConfigureAwait(false);
#else
                stream.Dispose();
#endif
            }
        }

        public virtual async Task SetBytesAsync(byte[] buffer, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            cancellationToken.ThrowIfCancellationRequested();

            var stream = await GetWriteStreamAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
#if NET8_0_OR_GREATER
                await stream.DisposeAsync().ConfigureAwait(false);
#else
                stream.Dispose();
#endif
            }
        }

        // === Metadata access (new API) ===

        /// <summary>
        /// Read the file's per-object metadata (content type, cache-control,
        /// user tags, driver-specific typed fields). When the driver already
        /// loaded the snapshot as part of an earlier operation (a strict
        /// <c>OpenFile</c> or <c>TryOpenFile</c> that paid a HEAD), the cached
        /// instance is returned immediately. Otherwise the driver fetches it
        /// once. Drivers without a per-object metadata surface return an empty
        /// snapshot.
        /// </summary>
        public virtual Task<FileMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FileMetadata());
        }

        /// <summary>Sync sibling of <see cref="GetMetadataAsync"/>.</summary>
        public virtual FileMetadata GetMetadata()
            => SyncBridge.Run(GetMetadataAsync);


        /// <summary>
        /// Copy <paramref name="source"/> into this file, applying
        /// <paramref name="options"/> at commit time and optionally reporting
        /// <paramref name="progress"/>.
        /// </summary>
        public virtual async Task CopyFromStreamAsync(Stream source, FileWriteOptions options = null, IProgress<TransferStatus> progress = null, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.CanRead) throw new NotSupportedException("The source stream does not support reading.");

            ThrowIfReadOnly();
            var total = progress != null ? source.Length : 0;
            var buffer = new byte[ReadBufferSize];
            int bytesRead;
            long transferred = 0;

            var destination = await GetWriteStreamAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                    if (progress != null)
                    {
                        transferred += bytesRead;
                        progress.Report(new TransferStatus(transferred, total));
                    }
                }
            }
            finally
            {
#if NET8_0_OR_GREATER
                await destination.DisposeAsync().ConfigureAwait(false);
#else
                destination.Dispose();
#endif
            }
        }

        public virtual async Task CopyToStreamAsync(
            Stream destination,
            IProgress<TransferStatus> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new NotSupportedException("The destination stream does not support writing.");

            using var source = await GetReadStreamAsync(cancellationToken).ConfigureAwait(false);
            if (progress == null)
            {
                await source.CopyToAsync(destination, ReadBufferSize, cancellationToken).ConfigureAwait(false);
                return;
            }

            var total = Length;
            long transferred = 0;
            var buffer = new byte[ReadBufferSize];
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                transferred += read;
                progress.Report(new TransferStatus(transferred, total));
            }
        }

        public virtual Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete();
            return Task.CompletedTask;
        }

        public virtual async Task<FileEntry> CopyToAsync(string newName, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            return await CopyToAsync(Parent, newName, progress, overwrite, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<FileEntry> CopyToAsync(DirectoryEntry directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!overwrite && await directory.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                throw new FileAlreadyExistsException(directory.CombineChildPath(name));

            var newFile = await directory.CreateFileAsync(name, overwrite, cancellationToken).ConfigureAwait(false);

            var writeStream = await newFile.GetWriteStreamAsync(options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await CopyToStreamAsync(writeStream, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
#if NET8_0_OR_GREATER
                await writeStream.DisposeAsync().ConfigureAwait(false);
#else
                writeStream.Dispose();
#endif
            }

            return newFile;
        }

        public virtual async Task<FileEntry> MoveToAsync(DirectoryEntry directory, string name, IProgress<TransferStatus> progress = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            var newFile = await CopyToAsync(directory, name, progress, overwrite, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(cancellationToken).ConfigureAwait(false);
            return newFile;
        }

        public virtual Task<FileEntry> RenameAsync(string newName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Rename(newName));
        }

        public override string ToString() => Path;
    }
}
