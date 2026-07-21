using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    /// <summary>
    /// Indicates that a directory can open a multipart write stream for a
    /// file by name, without the caller resolving a <see cref="FileEntry"/>
    /// handle first. The returned stream follows the
    /// <see cref="IMultipartUploadable"/> contract: bytes chunk into parts
    /// as they accumulate, disposing commits the upload, and an exception
    /// during a write aborts it.
    /// </summary>
    public interface IMultipartUploadableDirectory : IMultipartCapable
    {
        /// <summary>
        /// Opens a multipart write stream for the file at
        /// <paramref name="name"/> (nested paths accepted, same rules as
        /// <c>OpenFile</c>). The file materializes in the store when the
        /// stream is disposed. <paramref name="options"/> are bound to the
        /// object when the upload starts and take effect on commit;
        /// <c>null</c> uses backend defaults.
        /// </summary>
        Stream GetMultipartWriteStream(string name, FileWriteOptions options = null);

        /// <summary>Async version of <see cref="GetMultipartWriteStream"/>.</summary>
        Task<Stream> GetMultipartWriteStreamAsync(string name, FileWriteOptions options = null, CancellationToken cancellationToken = default);
    }
}
