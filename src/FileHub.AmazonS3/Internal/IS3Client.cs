using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.AmazonS3.Internal
{
    /// <summary>
    /// Narrow abstraction over the operations the FileHub driver needs from
    /// AWS S3. All driver classes depend only on this interface so the
    /// storage logic can be unit-tested with an in-memory fake, with no
    /// AWSSDK types leaking into the public API.
    /// </summary>
    internal interface IS3Client : IDisposable
    {
        string Bucket { get; }
        string Region { get; }

        /// <summary>
        /// Opaque identity shared by clients that talk to S3 with the same
        /// credentials. Reference equality of this object decides whether
        /// the driver can issue a server-side <c>CopyObject</c> across
        /// buckets — S3 routes such copies through a single authenticated
        /// client.
        /// </summary>
        object CredentialScope { get; }

        Task<S3HeadResult> HeadObjectAsync(string key, CancellationToken cancellationToken = default);

        Task<S3GetResult> GetObjectAsync(string key, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default);

        Task PutObjectAsync(
            string key,
            Stream body,
            long contentLength,
            string contentType,
            IReadOnlyDictionary<string, string> userMetadata,
            string storageClass,
            string serverSideEncryption,
            CancellationToken cancellationToken = default);

        Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Copies an object FROM <paramref name="sourceBucket"/> TO this
        /// client's bucket (<see cref="Bucket"/>). The receiver is always
        /// the <b>destination</b> client — S3 routes <c>CopyObject</c>
        /// through the destination's endpoint, so invoking this on the
        /// destination client is what makes cross-region copies work
        /// without a byte transfer through the caller.
        /// </summary>
        /// <summary>
        /// When <paramref name="metadataReplace"/> is <c>true</c> the
        /// destination object takes the supplied
        /// <paramref name="contentType"/>/<paramref name="userMetadata"/>/
        /// <paramref name="storageClass"/>/<paramref name="serverSideEncryption"/>
        /// via <c>MetadataDirective = REPLACE</c>. When <c>false</c>, the
        /// SDK default (<c>COPY</c>) applies and the destination inherits
        /// the source's metadata; the four fields are ignored.
        /// </summary>
        Task CopyFromBucketAsync(
            string sourceBucket,
            string sourceKey,
            string destinationKey,
            bool metadataReplace,
            string contentType,
            IReadOnlyDictionary<string, string> userMetadata,
            string storageClass,
            string serverSideEncryption,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists objects under <paramref name="prefix"/>. <paramref name="startAfter"/>
        /// is an exclusive cursor (S3 returns keys &gt; startAfter) honored
        /// only when <paramref name="continuationToken"/> is null — S3 ignores
        /// StartAfter once a paginated sequence is in progress.
        /// </summary>
        Task<S3ListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string continuationToken, string startAfter, CancellationToken cancellationToken = default);

        Task<S3BucketInfo> GetBucketAsync(CancellationToken cancellationToken = default);

        Task<string> GetPreSignedUrlAsync(string key, DateTime timeExpiresUtc, CancellationToken cancellationToken = default);

        Task<string> BeginMultipartUploadAsync(
            string key,
            string contentType,
            IReadOnlyDictionary<string, string> userMetadata,
            string storageClass,
            string serverSideEncryption,
            CancellationToken cancellationToken = default);

        /// <summary>Uploads one part. Returns the ETag returned by the store.</summary>
        Task<string> UploadPartAsync(string key, string uploadId, int partNumber, Stream body, long contentLength, CancellationToken cancellationToken = default);

        Task CompleteMultipartUploadAsync(string key, string uploadId, IReadOnlyList<S3CompletedPart> parts, CancellationToken cancellationToken = default);

        Task AbortMultipartUploadAsync(string key, string uploadId, CancellationToken cancellationToken = default);

        Task<Uri> GetPreSignedUploadPartUrlAsync(string key, string uploadId, int partNumber, DateTime expiresUtc, CancellationToken cancellationToken = default);
    }

    internal sealed class S3HeadResult
    {
        public long? ContentLength { get; set; }
        public DateTime? LastModified { get; set; }
        public string ContentType { get; set; }
        public Dictionary<string, string> UserMetadata { get; set; }
        public string StorageClass { get; set; }
        public string ServerSideEncryption { get; set; }
    }

    internal sealed class S3GetResult
    {
        public Stream InputStream { get; set; }
    }

    internal sealed class S3ListPage
    {
        public List<S3ListObject> Objects { get; set; } = new List<S3ListObject>();
        public List<string> Prefixes { get; set; } = new List<string>();
        public string NextContinuationToken { get; set; }
    }

    internal sealed class S3ListObject
    {
        public string Key { get; set; }
        public long? Size { get; set; }
        public DateTime? LastModified { get; set; }
    }

    internal sealed class S3BucketInfo
    {
        public bool IsPublic { get; set; }
    }

    internal sealed class S3CompletedPart
    {
        public int PartNumber { get; set; }
        public string ETag { get; set; }
    }
}
