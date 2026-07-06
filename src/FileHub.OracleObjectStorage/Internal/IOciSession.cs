using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage.Internal
{
    /// <summary>
    /// Per-FileHub shared state. Owns the <see cref="IOciClient"/> and memoizes
    /// the "is bucket public" probe so every file in the tree answers
    /// <c>IsPublic</c> without round-tripping to OCI after the first call.
    /// </summary>
    internal interface IOciSession : IDisposable
    {
        IOciClient Client { get; }

        bool GetIsPublic();

        Task<bool> GetIsPublicAsync(CancellationToken cancellationToken = default);
    }
}
