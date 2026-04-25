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

        bool GetIsPublic();

        Task<bool> GetIsPublicAsync(CancellationToken cancellationToken = default);
    }

    internal static class S3SessionTarget
    {
        /// <summary>
        /// Returns true when both clients are authenticated with the same
        /// credentials (same <see cref="IS3Client.CredentialScope"/>). S3
        /// routes server-side <c>CopyObject</c> across buckets through the
        /// authenticated client, so a shared scope is the correct gate —
        /// not matching <c>Bucket/Region</c>.
        /// </summary>
        public static bool SameCredentials(IS3Client a, IS3Client b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return ReferenceEquals(a.CredentialScope, b.CredentialScope);
        }
    }

    internal sealed class S3Session : IS3Session
    {
        // 0 = unknown, 1 = false, 2 = true.
        private int _isPublicState;
        private readonly SemaphoreSlim _publicAccessGate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public IS3Client Client { get; }

        public S3Session(IS3Client client)
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
                Volatile.Write(ref _isPublicState, info.IsPublic ? 2 : 1);
                return info.IsPublic;
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
            if (_disposed) throw new ObjectDisposedException(nameof(S3Session));
        }
    }
}
