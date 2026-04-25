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
    /// Contrast with the regular <see cref="FileEntry.GetWriteStream"/>,
    /// which buffers the full payload in memory before issuing a single
    /// request — fine for small files but unsuitable for large ones.
    /// </summary>
    public interface IMultipartUploadable
    {
        /// <summary>
        /// Minimum size (in bytes) the backing store accepts for any part
        /// except the last one. Writes below this threshold are buffered
        /// locally; crossing it triggers a part upload. S3 = 5 MiB.
        /// </summary>
        long MinimumPartSize { get; }

        /// <summary>
        /// Opens a write stream that transparently chunks data into
        /// multipart parts as bytes accumulate. Disposing / closing the
        /// stream flushes the trailing part and commits the upload; an
        /// exception during a write aborts the upload and discards any
        /// uploaded parts.
        /// </summary>
        Stream GetMultipartWriteStream();

        /// <summary>Async version of <see cref="GetMultipartWriteStream"/>.</summary>
        Task<Stream> GetMultipartWriteStreamAsync(CancellationToken cancellationToken = default);
    }
}
