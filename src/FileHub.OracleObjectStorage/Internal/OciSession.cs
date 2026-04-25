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

    internal static class OciSessionTarget
    {
        /// <summary>
        /// Returns true when both clients are authenticated with the same
        /// credentials (same <see cref="IOciClient.CredentialScope"/>). OCI
        /// routes server-side <c>CopyObject</c> across buckets, namespaces and
        /// regions through the authenticated client, so a shared scope is the
        /// correct gate — not matching <c>Namespace/Bucket/Region</c>.
        /// </summary>
        public static bool SameCredentials(IOciClient a, IOciClient b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return ReferenceEquals(a.CredentialScope, b.CredentialScope);
        }
    }

    internal sealed class OciSession : IOciSession
    {
        // 0 = unknown, 1 = false, 2 = true. Packed into an int so reads are
        // torn-free; the semaphore serialises the GetBucketAsync probe so only
        // one caller pays the round-trip.
        private int _isPublicState;
        private readonly SemaphoreSlim _publicAccessGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public IOciClient Client { get; }

        public OciSession(IOciClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public bool GetIsPublic() => SyncBridge.Run(ct => GetIsPublicAsync(ct));

        public async Task<bool> GetIsPublicAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            var cached = Volatile.Read(ref _isPublicState);
            if (cached != 0) return cached == 2;

            await _publicAccessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = Volatile.Read(ref _isPublicState);
                if (cached != 0) return cached == 2;

                ThrowIfDisposed();
                var info = await Client.GetBucketAsync(cancellationToken).ConfigureAwait(false);
                var value = info.PublicAccessType == OciBucketAccessType.ObjectRead
                    || info.PublicAccessType == OciBucketAccessType.ObjectReadWithoutList;
                Volatile.Write(ref _isPublicState, value ? 2 : 1);
                return value;
            }
            finally
            {
                _publicAccessGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _publicAccessGate.Dispose();
            Client.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OciSession));
        }
    }
}
