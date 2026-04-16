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

    internal sealed class OciSession : IOciSession
    {
        private readonly object _publicAccessGate = new object();
        private bool? _isPublicCached;
        private bool _disposed;

        public IOciClient Client { get; }

        public OciSession(IOciClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public bool GetIsPublic() => GetIsPublicAsync().GetAwaiter().GetResult();

        public async Task<bool> GetIsPublicAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            lock (_publicAccessGate)
            {
                if (_isPublicCached.HasValue) return _isPublicCached.Value;
            }

            var info = await Client.GetBucketAsync(cancellationToken).ConfigureAwait(false);
            var value = info.PublicAccessType == OciBucketAccessType.ObjectRead
                || info.PublicAccessType == OciBucketAccessType.ObjectReadWithoutList;

            lock (_publicAccessGate) _isPublicCached = value;
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Client.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OciSession));
        }
    }
}
