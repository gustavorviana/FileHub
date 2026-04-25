using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    /// <summary>
    /// Indicates that a file supports multipart upload delegated to a
    /// remote client via pre-signed URLs. The backend initiates the
    /// upload and hands out N signed PUT URLs; a client (browser, mobile
    /// app, remote worker) uploads the parts directly to the store and
    /// returns the ETags; the backend then calls
    /// <see cref="CompleteSignedMultipartUploadAsync"/> to finalize.
    ///
    /// Use when the backend does not hold the bytes — typically when
    /// offloading large uploads from the web tier to clients.
    /// </summary>
    public interface IMultipartUploadSignable
    {
        /// <summary>Minimum part size (bytes) accepted by the backing store.</summary>
        long MinimumPartSize { get; }

        // === Sync (delegates to async) ===

        SignedMultipartUpload BeginSignedMultipartUpload(MultipartUploadSpec spec, TimeSpan expiresIn);

        void CompleteSignedMultipartUpload(string uploadId, IReadOnlyList<UploadedPart> parts);

        void AbortSignedMultipartUpload(string uploadId);

        // === Async (source of truth) ===

        /// <summary>
        /// Begins a multipart upload and returns one pre-signed PUT URL
        /// for each part described by <paramref name="spec"/>, valid for
        /// <paramref name="expiresIn"/>. Distribute the URLs to the
        /// remote client; collect ETags when they finish.
        /// </summary>
        Task<SignedMultipartUpload> BeginSignedMultipartUploadAsync(
            MultipartUploadSpec spec,
            TimeSpan expiresIn,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Finalizes the upload. <paramref name="parts"/> must contain
        /// every ETag returned by the client — one per part, in any
        /// order. The file materializes in the store when this returns.
        /// </summary>
        Task CompleteSignedMultipartUploadAsync(
            string uploadId,
            IReadOnlyList<UploadedPart> parts,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels the upload and discards any parts already uploaded.
        /// Use when the client gave up or failed to return ETags; leaves
        /// no orphan parts billed by the store.
        /// </summary>
        Task AbortSignedMultipartUploadAsync(
            string uploadId,
            CancellationToken cancellationToken = default);
    }
}
