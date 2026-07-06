using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    /// <summary>
    /// Capability: the directory can mint a time-limited pre-signed URL that
    /// a remote client (browser, mobile app, remote worker) can <c>PUT</c>
    /// bytes to, materialising a new file under this directory without the
    /// backend ever touching the payload. Typical use: large user uploads
    /// offloaded straight from the client to object storage.
    /// <para>
    /// Implemented by object-storage drivers (Amazon S3, Oracle Object
    /// Storage). Local / Memory / FTP do not implement this interface.
    /// </para>
    /// </summary>
    public interface ISignedUploadable
    {
        /// <summary>
        /// Returns a pre-signed upload URL for a new file named <paramref name="name"/>
        /// (single segment or nested path) under this directory. The target object
        /// does <b>not</b> need to exist beforehand — the first <c>PUT</c> to the
        /// URL creates it. If an object with that key already exists, the <c>PUT</c>
        /// overwrites it.
        /// <para>
        /// <paramref name="options"/> (when provided) are baked into the URL
        /// signature: the remote client is then required to send the matching
        /// <c>Content-Type</c> / <c>Cache-Control</c> / user-metadata headers
        /// on the PUT request, otherwise the backend rejects with a signature
        /// mismatch. Drivers that cannot bind headers to the signature
        /// (currently OCI) ignore <paramref name="options"/> silently — the
        /// client is then free to send any headers.
        /// </para>
        /// </summary>
        /// <param name="name">File name or relative path under this directory.</param>
        /// <param name="expiresIn">Duration until the URL expires.</param>
        /// <param name="options">Optional headers to bind into the signature.</param>
        Task<Uri> GetSignedUploadUrlAsync(string name, TimeSpan expiresIn, FileWriteOptions options = null, CancellationToken cancellationToken = default);

        /// <summary>Sync version of <see cref="GetSignedUploadUrlAsync(string, TimeSpan, FileWriteOptions, CancellationToken)"/>.</summary>
        Uri GetSignedUploadUrl(string name, TimeSpan expiresIn, FileWriteOptions options = null);
    }
}
