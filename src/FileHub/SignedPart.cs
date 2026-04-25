using System;

namespace FileHub
{
    /// <summary>
    /// A single pre-signed PUT URL for one part of a multipart upload.
    /// Returned inside <see cref="SignedMultipartUpload"/> by
    /// <see cref="IMultipartUploadSignable.BeginSignedMultipartUploadAsync"/>.
    /// </summary>
    public sealed class SignedPart
    {
        /// <summary>1-based part number — S3 requires parts numbered 1..N.</summary>
        public int PartNumber { get; }

        /// <summary>Pre-signed PUT URL. The remote client uploads bytes directly to this URL.</summary>
        public Uri UploadUrl { get; }

        /// <summary>
        /// Expected bytes for this part, derived from
        /// <see cref="MultipartUploadSpec.GetPartLength"/>. The last part may
        /// carry less than <see cref="MultipartUploadSpec.PartSize"/>.
        /// </summary>
        public long ContentLength { get; }

        public SignedPart(int partNumber, Uri uploadUrl, long contentLength)
        {
            if (partNumber < 1) throw new ArgumentOutOfRangeException(nameof(partNumber));
            if (uploadUrl == null) throw new ArgumentNullException(nameof(uploadUrl));
            if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));
            PartNumber = partNumber;
            UploadUrl = uploadUrl;
            ContentLength = contentLength;
        }
    }
}
