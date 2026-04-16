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

        Task<OciHeadResult> HeadObjectAsync(string objectName, CancellationToken cancellationToken = default);

        Task<OciGetResult> GetObjectAsync(string objectName, long? rangeStart, long? rangeEndInclusive, CancellationToken cancellationToken = default);

        Task PutObjectAsync(
            string objectName,
            Stream body,
            long contentLength,
            string contentType,
            IReadOnlyDictionary<string, string> opcMeta,
            CancellationToken cancellationToken = default);

        Task DeleteObjectAsync(string objectName, CancellationToken cancellationToken = default);

        Task RenameObjectAsync(string sourceName, string newName, CancellationToken cancellationToken = default);

        Task CopyObjectAsync(string sourceObjectName, string destinationObjectName, CancellationToken cancellationToken = default);

        Task<OciListPage> ListObjectsAsync(string prefix, string delimiter, int? limit, string start, CancellationToken cancellationToken = default);

        Task<OciBucketInfo> GetBucketAsync(CancellationToken cancellationToken = default);

        Task<string> CreatePreauthenticatedReadRequestAsync(string objectName, string parName, DateTime timeExpiresUtc, CancellationToken cancellationToken = default);
    }

    internal sealed class OciHeadResult
    {
        public long? ContentLength { get; set; }
        public DateTime? LastModified { get; set; }
        public string ContentType { get; set; }
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
