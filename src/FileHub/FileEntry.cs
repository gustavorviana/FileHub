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
        public abstract Stream GetWriteStream();
        public abstract void Delete();
        public abstract FileEntry Rename(string newName);
        public abstract FileEntry MoveTo(FileDirectory directory, string name);

        // === Sync convenience (implemented using streams) ===

        public string ReadAllText()
        {
            return ReadAllText(Encoding.UTF8);
        }

        public string ReadAllText(Encoding encoding)
        {
            using (var stream = GetReadStream())
            using (var reader = new StreamReader(stream, encoding))
                return reader.ReadToEnd();
        }

        public byte[] ReadAllBytes()
        {
            using (var ms = new MemoryStream())
            using (var stream = GetReadStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public void SetText(string content, Encoding encoding = null)
        {
            ThrowIfReadOnly();
            var bytes = (encoding ?? Encoding.UTF8).GetBytes(content);
            using (var stream = GetWriteStream())
                stream.Write(bytes, 0, bytes.Length);
        }

        public void SetBytes(byte[] buffer)
        {
            ThrowIfReadOnly();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            using (var stream = GetWriteStream())
                stream.Write(buffer, 0, buffer.Length);
        }

        public void CopyToStream(Stream destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new NotSupportedException("The destination stream does not support writing.");

            byte[] buffer = new byte[81920];
            int bytesRead;
            using (var source = GetReadStream())
                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                    destination.Write(buffer, 0, bytesRead);
        }

        public virtual FileEntry CopyTo(string newName)
        {
            return CopyTo(Parent, newName);
        }

        public virtual FileEntry CopyTo(FileDirectory directory, string name)
        {
            var newFile = directory.CreateFile(name);
            using (var writeStream = newFile.GetWriteStream())
                CopyToStream(writeStream);
            return newFile;
        }

        // === Async defaults (wrap sync - cloud drivers override) ===

        public virtual Task<Stream> GetReadStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetReadStream());
        }

        public virtual Task<Stream> GetWriteStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetWriteStream());
        }

        public virtual async Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
        {
            return await ReadAllTextAsync(Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = await GetReadStreamAsync(cancellationToken).ConfigureAwait(false);
            using (stream)
            using (var reader = new StreamReader(stream, encoding))
                return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        public virtual async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = await GetReadStreamAsync(cancellationToken).ConfigureAwait(false);
            using (stream)
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                return ms.ToArray();
            }
        }

        public virtual async Task SetTextAsync(string content, Encoding encoding = null, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = (encoding ?? Encoding.UTF8).GetBytes(content);
            var stream = await GetWriteStreamAsync(cancellationToken).ConfigureAwait(false);
            using (stream)
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task SetBytesAsync(byte[] buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfReadOnly();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            cancellationToken.ThrowIfCancellationRequested();
            var stream = await GetWriteStreamAsync(cancellationToken).ConfigureAwait(false);
            using (stream)
                await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task CopyToStreamAsync(Stream destination, CancellationToken cancellationToken = default)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new NotSupportedException("The destination stream does not support writing.");

            var source = await GetReadStreamAsync(cancellationToken).ConfigureAwait(false);
            using (source)
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
        }

        public virtual Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delete();
            return Task.CompletedTask;
        }

        public virtual async Task<FileEntry> CopyToAsync(string newName, CancellationToken cancellationToken = default)
        {
            return await CopyToAsync(Parent, newName, cancellationToken).ConfigureAwait(false);
        }

        public virtual async Task<FileEntry> CopyToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newFile = await directory.CreateFileAsync(name, cancellationToken).ConfigureAwait(false);
            var writeStream = await newFile.GetWriteStreamAsync(cancellationToken).ConfigureAwait(false);
            using (writeStream)
                await CopyToStreamAsync(writeStream, cancellationToken).ConfigureAwait(false);
            return newFile;
        }

        public virtual async Task<FileEntry> MoveToAsync(FileDirectory directory, string name, CancellationToken cancellationToken = default)
        {
            var newFile = await CopyToAsync(directory, name, cancellationToken).ConfigureAwait(false);
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
