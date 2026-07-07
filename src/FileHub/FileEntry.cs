using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    public abstract class FileEntry : FileSystemEntry
    {
        public virtual string Extension => System.IO.Path.GetExtension(Name);
        public abstract long Length { get; }
        public abstract FileDirectory Parent { get; }

        protected FileEntry(string name) : base(name) { }

        // === Sync abstract (drivers implement) ===

        public abstract Stream GetReadStream();
        public abstract Stream GetWriteStream(FileWriteOptions options = null);
        public abstract void Delete();
        public abstract FileEntry Rename(string newName);
        public abstract FileEntry MoveTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null);

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

            var buffer = new byte[81920];
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
            var buffer = new byte[81920];
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

        public virtual FileEntry CopyTo(string newName, IProgress<TransferStatus> progress = null)
            => CopyTo(Parent, newName, progress);

        public virtual FileEntry CopyTo(FileDirectory directory, string name, IProgress<TransferStatus> progress = null)
        {
            var newFile = directory.CreateFile(name);
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

            await stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
            return ms.ToArray();
        }

        public virtual async Task SetTextAsync(string content, Encoding encoding = null, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = (encoding ?? Encoding.UTF8).GetBytes(content);

            using var stream = await GetWriteStreamAsync(options, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task SetBytesAsync(byte[] buffer, FileWriteOptions options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = await GetWriteStreamAsync(options, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
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
            var buffer = new byte[81920];
            int bytesRead;
            long transferred = 0;

            using var destination = await GetWriteStreamAsync(options, cancellationToken).ConfigureAwait(false);

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
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                return;
            }

            var total = Length;
            long transferred = 0;
            var buffer = new byte[81920];
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

        public virtual async Task<FileEntry> CopyToAsync(string newName, IProgress<TransferStatus> progress = null, CancellationToken cancellationToken = default)
        {
            return await CopyToAsync(Parent, newName, progress, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newFile = await directory.CreateFileAsync(name, cancellationToken).ConfigureAwait(false);

            using var writeStream = await newFile.GetWriteStreamAsync(options: null, cancellationToken).ConfigureAwait(false);
            await CopyToStreamAsync(writeStream, progress, cancellationToken).ConfigureAwait(false);

            return newFile;
        }

        public virtual async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, IProgress<TransferStatus> progress = null, CancellationToken cancellationToken = default)
        {
            var newFile = await CopyToAsync(directory, name, progress, cancellationToken).ConfigureAwait(false);
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
