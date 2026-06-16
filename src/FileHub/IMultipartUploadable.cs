using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    /// <summary>
    /// Indicates that a file supports multipart upload with the backend
    /// streaming bytes to the store in chunks. Use when the process
    /// writing the file also holds the bytes (server-side generation,
    /// long-running import, uploading a local stream to an object store).
    ///
    /// Contrast with the regular <see cref="FileEntry.GetWriteStream(FileWriteOptions)"/>,
    /// which buffers the full payload in memory before issuing a single
    /// request — fine for small files but unsuitable for large ones.
    /// </summary>
    public interface IMultipartUploadable
    {
        /// <summary>
        /// Minimum size (in bytes) the backing store accepts for any part
        /// except the last one. Writes below this threshold are buffered
        /// locally; crossing it triggers a part upload. Common object-storage
        /// backends require parts of at least 5 MiB.
        /// </summary>
        long MinimumPartSize { get; }

        /// <summary>
        /// Opens a write stream that transparently chunks data into multipart
        /// parts as bytes accumulate. Disposing / closing the stream flushes the
        /// trailing part and commits the upload; an exception during a write
        /// aborts the upload and discards any uploaded parts.
        /// <para>
        /// <paramref name="options"/> (content type, cache-control, user
        /// metadata, and any driver-specific fields such as S3 storage class /
        /// SSE) are applied to the object on commit. Drivers ignore fields they
        /// don't support — never throw. <c>null</c> uses backend defaults.
        /// </para>
        /// </summary>
        Stream GetMultipartWriteStream(FileWriteOptions options = null);

        /// <summary>Async version of <see cref="GetMultipartWriteStream"/>.</summary>
        Task<Stream> GetMultipartWriteStreamAsync(FileWriteOptions options = null, CancellationToken cancellationToken = default);
    }
}
