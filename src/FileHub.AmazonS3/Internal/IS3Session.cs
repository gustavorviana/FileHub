using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.AmazonS3.Internal
{
    /// <summary>
    /// Per-FileHub shared state. Owns the <see cref="IS3Client"/> and
    /// memoizes the "is bucket public" probe so every file in the tree
    /// answers <c>IsPublic</c> without round-tripping to S3 after the
    /// first call.
    /// </summary>
    internal interface IS3Session : IDisposable
    {
        IS3Client Client { get; }
        MultipartStreamOptions Multipart { get; }

        bool GetIsPublic();

        Task<bool> GetIsPublicAsync(CancellationToken cancellationToken = default);
    }
}
