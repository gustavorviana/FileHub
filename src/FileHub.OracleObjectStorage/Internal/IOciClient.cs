using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage.Internal
{
    /// <summary>
    /// Narrow abstraction over the operations the FileHub driver needs from
    /// Oracle Cloud Infrastructure Object Storage. All driver classes depend
    /// only on this interface so the storage logic can be unit-tested with
    /// an in-memory fake, with no SDK types leaking into the public API.
    /// </summary>
    internal interface IOciClient : IDisposable
    {
        string Namespace { get; }
        string Bucket { get; }
        string Region { get; }

        /// <summary>
        /// Opaque identity shared by clients that talk to OCI with the same
        /// credentials. Reference equality of this object decides whether the
        /// driver can issue a server-side <c>CopyObject</c> across buckets,
        /// namespaces or regions — OCI routes such copies through a single
        /// authenticated <c>ObjectStorageClient</c>.
        /// </summary>
        object CredentialScope { get; }

        Task<OciHeadResult> HeadObjectAsync(string objectName, CancellationToken cancellationToken = default);

        Task<OciGetResult> GetObjectAsync(string objectName, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default);

        Task PutObjectAsync(
            string objectName,
            Stream body,
            long contentLength,
            OciWriteOptions options,
            CancellationToken cancellationToken = default);

        Task DeleteObjectAsync(string objectName, CancellationToken cancellationToken = default);

        Task RenameObjectAsync(string sourceName, string newName, CancellationToken cancellationToken = default);

        Task<IOciWorkRequestHandle> CopyObjectAsync(
            string sourceObjectName,
            string destinationNamespace,
            string destinationBucket,
            string destinationRegion,
            string destinationObjectName,
            CancellationToken cancellationToken = default);

        Task<OciListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string start, CancellationToken cancellationToken = default);

        Task<OciBucketInfo> GetBucketAsync(CancellationToken cancellationToken = default);

        Task<string> CreatePreauthenticatedReadRequestAsync(string objectName, string parName, DateTime timeExpiresUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Pre-authenticated request granting <c>ObjectWrite</c> access — the
        /// caller can <c>PUT</c> bytes to the returned access URI without
        /// authenticating against OCI.
        /// </summary>
        Task<string> CreatePreauthenticatedWriteRequestAsync(string objectName, string parName, DateTime timeExpiresUtc, CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts a multipart upload for <paramref name="objectName"/>. The
        /// write options (content type, cache-control, metadata) are bound to
        /// the object here — OCI applies them when the upload is committed.
        /// Returns the upload id used by the other multipart operations.
        /// </summary>
        Task<string> CreateMultipartUploadAsync(string objectName, OciWriteOptions options, CancellationToken cancellationToken = default);

        /// <summary>Uploads one part. Returns the ETag returned by the store.</summary>
        Task<string> UploadPartAsync(string objectName, string uploadId, int partNumber, Stream body, long contentLength, CancellationToken cancellationToken = default);

        Task CommitMultipartUploadAsync(string objectName, string uploadId, IReadOnlyList<OciCompletedPart> parts, CancellationToken cancellationToken = default);

        Task AbortMultipartUploadAsync(string objectName, string uploadId, CancellationToken cancellationToken = default);
    }

    internal sealed class OciCompletedPart
    {
        public int PartNumber { get; set; }
        public string ETag { get; set; }
    }

    internal sealed class OciHeadResult
    {
        public long? ContentLength { get; set; }
        public DateTime? LastModified { get; set; }
        public string ContentType { get; set; }
        public string CacheControl { get; set; }
        public Dictionary<string, string> OpcMeta { get; set; }
    }

    internal sealed class OciGetResult
    {
        public Stream InputStream { get; set; }
    }

    internal sealed class OciListPage
    {
        public List<OciListObject> Objects { get; set; } = new List<OciListObject>();
        public List<string> Prefixes { get; set; } = new List<string>();
        public string NextStartWith { get; set; }
    }

    internal sealed class OciListObject
    {
        public string Name { get; set; }
        public long? Size { get; set; }
        public DateTime? TimeCreated { get; set; }
    }

    internal sealed class OciBucketInfo
    {
        public OciBucketAccessType PublicAccessType { get; set; }
    }

    internal enum OciBucketAccessType
    {
        NoPublicAccess,
        ObjectRead,
        ObjectReadWithoutList
    }
}
