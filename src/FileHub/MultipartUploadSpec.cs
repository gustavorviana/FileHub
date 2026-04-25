using System;

namespace FileHub
{
    /// <summary>
    /// Describes how to split a large upload into parts for multipart APIs
    /// (S3 multipart, Azure block blobs, GCS resumable uploads, etc.).
    /// Drivers read <see cref="TotalBytes"/>, <see cref="PartSize"/>, and
    /// <see cref="PartCount"/> to generate the right number of parts and
    /// validate against backend-specific limits (S3: parts must be at
    /// least 5 MiB except the last; at most 10,000 parts per upload).
    /// </summary>
    public readonly struct MultipartUploadSpec : IEquatable<MultipartUploadSpec>
    {
        public long TotalBytes { get; }
        public long PartSize { get; }
        public int PartCount { get; }

        private MultipartUploadSpec(long totalBytes, long partSize, int partCount)
        {
            TotalBytes = totalBytes;
            PartSize = partSize;
            PartCount = partCount;
        }

        /// <summary>
        /// Split <paramref name="totalBytes"/> into parts of
        /// <paramref name="partSize"/> bytes each. The last part carries
        /// the remainder and may be smaller.
        /// </summary>
        public static MultipartUploadSpec FromPartSize(long totalBytes, long partSize)
        {
            if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes), "Total bytes must be positive.");
            if (partSize <= 0) throw new ArgumentOutOfRangeException(nameof(partSize), "Part size must be positive.");
            var count = checked((int)((totalBytes + partSize - 1) / partSize));
            return new MultipartUploadSpec(totalBytes, partSize, count);
        }

        /// <summary>
        /// Split <paramref name="totalBytes"/> into exactly
        /// <paramref name="partCount"/> evenly-sized parts. The last part
        /// may be slightly smaller to absorb the remainder.
        /// </summary>
        public static MultipartUploadSpec FromPartCount(long totalBytes, int partCount)
        {
            if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes), "Total bytes must be positive.");
            if (partCount <= 0) throw new ArgumentOutOfRangeException(nameof(partCount), "Part count must be positive.");
            var size = (totalBytes + partCount - 1) / partCount;
            return new MultipartUploadSpec(totalBytes, size, partCount);
        }

        /// <summary>
        /// Content length (bytes) of part <paramref name="partNumber"/>
        /// (1-based). Equal to <see cref="PartSize"/> except for the last
        /// part, which carries the remainder.
        /// </summary>
        public long GetPartLength(int partNumber)
        {
            if (partNumber < 1 || partNumber > PartCount)
                throw new ArgumentOutOfRangeException(nameof(partNumber));
            if (partNumber < PartCount) return PartSize;
            var usedBeforeLast = PartSize * (PartCount - 1);
            return TotalBytes - usedBeforeLast;
        }

        public bool Equals(MultipartUploadSpec other)
            => TotalBytes == other.TotalBytes && PartSize == other.PartSize && PartCount == other.PartCount;

        public override bool Equals(object obj) => obj is MultipartUploadSpec other && Equals(other);

        public override int GetHashCode()
            => unchecked((int)(TotalBytes ^ (TotalBytes >> 32) ^ PartSize ^ (PartSize >> 32) ^ PartCount));

        public override string ToString()
            => $"TotalBytes={TotalBytes}, PartSize={PartSize}, PartCount={PartCount}";

        public static bool operator ==(MultipartUploadSpec a, MultipartUploadSpec b) => a.Equals(b);
        public static bool operator !=(MultipartUploadSpec a, MultipartUploadSpec b) => !a.Equals(b);
    }
}
