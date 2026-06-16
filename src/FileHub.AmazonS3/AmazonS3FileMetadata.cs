namespace FileHub.AmazonS3
{
    /// <summary>
    /// Read-only S3-specific metadata surface. Adds typed per-object fields
    /// (<see cref="StorageClass"/>, <see cref="ServerSideEncryption"/>) on top
    /// of the base <see cref="FileMetadata"/> (<see cref="FileMetadata.Tags"/>,
    /// <see cref="FileMetadata.ContentType"/>, <see cref="FileMetadata.CacheControl"/>).
    /// To change values, pass <see cref="S3WriteOptions"/> to the next write call.
    /// </summary>
    public sealed class AmazonS3FileMetadata : FileMetadata
    {
        /// <summary>
        /// S3 storage class — "STANDARD" (default), "STANDARD_IA",
        /// "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER", "DEEP_ARCHIVE",
        /// "GLACIER_IR". <c>null</c> means "bucket default" on writes, and
        /// "unknown / not reported" after reads.
        /// </summary>
        public string StorageClass { get; protected internal set; }

        /// <summary>
        /// Server-side encryption: "AES256" (SSE-S3) or "aws:kms"
        /// (SSE-KMS). <c>null</c> = bucket default / no explicit header.
        /// </summary>
        public string ServerSideEncryption { get; protected internal set; }

        /// <summary>
        /// Driver-internal: load all fields from a server HEAD response.
        /// </summary>
        internal void LoadFromHead(
            System.Collections.Generic.IReadOnlyDictionary<string, string> tags,
            string storageClass,
            string contentType,
            string serverSideEncryption)
        {
            SetTags(tags);
            StorageClass = storageClass;
            ContentType = contentType;
            ServerSideEncryption = serverSideEncryption;
        }
    }
}
