namespace FileHub.AmazonS3
{
    /// <summary>
    /// Mutable S3-specific metadata surface. Adds typed per-object fields
    /// (<see cref="StorageClass"/>, <see cref="ContentType"/>,
    /// <see cref="ServerSideEncryption"/>) on top of the base
    /// <see cref="FileMetadata.Tags"/>. Mutating any setter flips
    /// <see cref="FileMetadata.IsModified"/>; the driver applies staged
    /// values on the next alteration op and clears the flag.
    /// </summary>
    public sealed class AmazonS3FileMetadata : FileMetadata
    {
        private string _storageClass;
        private string _contentType;
        private string _serverSideEncryption;

        /// <summary>
        /// S3 storage class — "STANDARD" (default), "STANDARD_IA",
        /// "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER", "DEEP_ARCHIVE",
        /// "GLACIER_IR". <c>null</c> means "bucket default" on writes, and
        /// "unknown / not reported" after reads.
        /// </summary>
        public string StorageClass
        {
            get => _storageClass;
            set
            {
                if (_storageClass == value) return;
                _storageClass = value;
                MarkModified();
            }
        }

        /// <summary>MIME type; sent as <c>Content-Type</c> on write.</summary>
        public string ContentType
        {
            get => _contentType;
            set
            {
                if (_contentType == value) return;
                _contentType = value;
                MarkModified();
            }
        }

        /// <summary>
        /// Server-side encryption: "AES256" (SSE-S3) or "aws:kms"
        /// (SSE-KMS). <c>null</c> = bucket default / no explicit header.
        /// </summary>
        public string ServerSideEncryption
        {
            get => _serverSideEncryption;
            set
            {
                if (_serverSideEncryption == value) return;
                _serverSideEncryption = value;
                MarkModified();
            }
        }

        /// <summary>
        /// Loads all fields from a server HEAD response without flipping
        /// <see cref="FileMetadata.IsModified"/>. Driver-only — use
        /// <c>Refresh</c> or <c>TryOpenFile</c> from user code.
        /// </summary>
        internal void LoadSynced(
            System.Collections.Generic.IReadOnlyDictionary<string, string> tags,
            string storageClass,
            string contentType,
            string serverSideEncryption)
        {
            LoadSynced(tags);
            _storageClass = storageClass;
            _contentType = contentType;
            _serverSideEncryption = serverSideEncryption;
        }
    }
}
