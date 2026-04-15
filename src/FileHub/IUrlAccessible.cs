using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    /// <summary>
    /// Indicates that a file system item can be accessed through a URL.
    /// Implemented by drivers that expose items over HTTP (S3, Oracle OCI,
    /// local public directories, etc).
    /// </summary>
    public interface IUrlAccessible
    {
        /// <summary>
        /// Whether this item can be accessed publicly without authentication.
        /// When true, <see cref="GetPublicUrl"/> returns a permanent URL.
        /// When false, use <see cref="GetSignedUrl"/> to generate a temporary access URL.
        /// </summary>
        bool IsPublic { get; }

        /// <summary>
        /// Returns the permanent public URL for this item.
        /// Only valid when <see cref="IsPublic"/> is true.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the item is not public.
        /// </exception>
        Uri GetPublicUrl();

        /// <summary>
        /// Returns a time-limited pre-signed URL that grants temporary access to this item.
        /// Works for both public and private items.
        /// </summary>
        /// <param name="expiresIn">Duration until the URL expires.</param>
        Uri GetSignedUrl(TimeSpan expiresIn);

        /// <summary>
        /// Async version of <see cref="GetSignedUrl(TimeSpan)"/>.
        /// Drivers that hit a network to generate the signature should override this.
        /// </summary>
        Task<Uri> GetSignedUrlAsync(TimeSpan expiresIn, CancellationToken cancellationToken = default);
    }
}
