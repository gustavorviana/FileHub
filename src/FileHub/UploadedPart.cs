using System;

namespace FileHub
{
    /// <summary>
    /// Receipt for a part uploaded by the remote client via a
    /// <see cref="SignedPart.UploadUrl"/>. The client collects the ETag
    /// from each PUT response and returns the list back to the backend,
    /// which passes it to
    /// <see cref="IMultipartUploadSignable.CompleteSignedMultipartUploadAsync"/>
    /// to finalize the upload.
    /// </summary>
    public sealed class UploadedPart
    {
        /// <summary>Matches the <see cref="SignedPart.PartNumber"/> that was uploaded.</summary>
        public int PartNumber { get; }

        /// <summary>ETag header value returned by the store for this part.</summary>
        public string ETag { get; }

        public UploadedPart(int partNumber, string eTag)
        {
            if (partNumber < 1) throw new ArgumentOutOfRangeException(nameof(partNumber));
            if (string.IsNullOrEmpty(eTag)) throw new ArgumentException("ETag cannot be null or empty.", nameof(eTag));
            PartNumber = partNumber;
            ETag = eTag;
        }
    }
}
