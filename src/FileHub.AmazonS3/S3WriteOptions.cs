namespace FileHub.AmazonS3
{
    /// <summary>
    /// S3-specific write options. Extends <see cref="FileWriteOptions"/>
    /// with the typed fields the S3 protocol exposes per object —
    /// storage class and server-side encryption.
    /// <para>
    /// Pass through the same parameter as <see cref="FileWriteOptions"/>;
    /// the driver downcasts when needed:
    /// </para>
    /// <code>
    /// await file.SetBytesAsync(bytes, new S3WriteOptions
    /// {
    ///     ContentType          = "image/png",
    ///     StorageClass         = "GLACIER",
    ///     ServerSideEncryption = "AES256",
    /// }, ct);
    /// </code>
    /// </summary>
    public class S3WriteOptions : FileWriteOptions
    {
        /// <summary>
        /// S3 storage class: <c>STANDARD</c> (default), <c>STANDARD_IA</c>,
        /// <c>ONEZONE_IA</c>, <c>INTELLIGENT_TIERING</c>, <c>GLACIER</c>,
        /// <c>DEEP_ARCHIVE</c>, <c>GLACIER_IR</c>. <c>null</c> = bucket default.
        /// </summary>
        public string StorageClass { get; set; }

        /// <summary>
        /// Server-side encryption: <c>AES256</c> (SSE-S3) or <c>aws:kms</c>
        /// (SSE-KMS). <c>null</c> = bucket default / omit header.
        /// </summary>
        public string ServerSideEncryption { get; set; }

        /// <summary>
        /// Per-write multipart policy. <c>null</c> inherits
        /// <see cref="AmazonS3HubOptions.Multipart"/> from the hub.
        /// </summary>
        public MultipartStreamOptions Multipart { get; set; }
    }
}
