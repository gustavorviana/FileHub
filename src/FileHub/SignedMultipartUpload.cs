using System;
using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Result of <see cref="IMultipartUploadSignable.BeginSignedMultipartUploadAsync"/>.
    /// Carries the store-assigned upload identifier plus one pre-signed
    /// PUT URL per part, ready to be handed off to a remote client.
    /// </summary>
    public sealed class SignedMultipartUpload
    {
        /// <summary>
        /// Opaque upload identifier. Must be passed back to
        /// <see cref="IMultipartUploadSignable.CompleteSignedMultipartUploadAsync"/>
        /// or <see cref="IMultipartUploadSignable.AbortSignedMultipartUploadAsync"/>.
        /// </summary>
        public string UploadId { get; }

        /// <summary>The spec used to compute the part layout.</summary>
        public MultipartUploadSpec Spec { get; }

        /// <summary>
        /// One pre-signed PUT URL per part, ordered by
        /// <see cref="SignedPart.PartNumber"/>.
        /// </summary>
        public IReadOnlyList<SignedPart> Parts { get; }

        public SignedMultipartUpload(string uploadId, MultipartUploadSpec spec, IReadOnlyList<SignedPart> parts)
        {
            if (string.IsNullOrEmpty(uploadId)) throw new ArgumentException("UploadId cannot be null or empty.", nameof(uploadId));
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            UploadId = uploadId;
            Spec = spec;
            Parts = parts;
        }
    }
}
