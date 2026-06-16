using System.Collections.Generic;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// Immutable read-only S3-specific metadata surface. Adds typed per-object
    /// fields (<see cref="StorageClass"/>, <see cref="ServerSideEncryption"/>)
    /// on top of the base <see cref="FileMetadata"/> (<see cref="FileMetadata.Tags"/>,
    /// <see cref="FileMetadata.ContentType"/>, <see cref="FileMetadata.CacheControl"/>).
    /// To change values, pass <see cref="S3WriteOptions"/> to the next write call.
    /// </summary>
    public sealed class AmazonS3FileMetadata : FileMetadata
    {
        public AmazonS3FileMetadata(
            string contentType = null,
            string cacheControl = null,
            IReadOnlyDictionary<string, string> tags = null,
            string storageClass = null,
            string serverSideEncryption = null)
            : base(contentType, cacheControl, tags)
        {
            StorageClass = storageClass;
            ServerSideEncryption = serverSideEncryption;
        }

        /// <summary>
        /// S3 storage class — "STANDARD" (default), "STANDARD_IA",
        /// "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER", "DEEP_ARCHIVE",
        /// "GLACIER_IR". <c>null</c> means "bucket default" on writes, and
        /// "unknown / not reported" after reads.
        /// </summary>
        public string StorageClass { get; }

        /// <summary>
        /// Server-side encryption: "AES256" (SSE-S3) or "aws:kms"
        /// (SSE-KMS). <c>null</c> = bucket default / no explicit header.
        /// </summary>
        public string ServerSideEncryption { get; }
    }
}
