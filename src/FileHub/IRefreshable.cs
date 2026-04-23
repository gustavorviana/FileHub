using System.Threading;
using System.Threading.Tasks;

namespace FileHub
{
    /// <summary>
    /// Implemented by <see cref="FileSystemEntry"/> types whose metadata is
    /// cached locally and needs to be re-fetched from the backing store
    /// explicitly — typically cloud / network drivers like OCI Object Storage
    /// and FTP. Drivers backed by the local filesystem usually don't need this.
    /// </summary>
    /// <remarks>
    /// Exists so callers can decide, in a context-appropriate way, whether to
    /// block on <see cref="Refresh"/> or await <see cref="RefreshAsync"/>.
    /// Property getters on <see cref="FileEntry"/> and <see cref="FileDirectory"/>
    /// are expected to be cheap and non-blocking; performing hidden
    /// async-over-sync I/O inside a getter risks deadlocks under UI or
    /// ASP.NET (classic) <c>SynchronizationContext</c>s, so drivers should
    /// cache whatever they know and require explicit refresh for freshness.
    /// </remarks>
    public interface IRefreshable
    {
        /// <summary>
        /// Synchronously re-fetches this entity's metadata from the backing
        /// store. Implementations typically delegate to <see cref="RefreshAsync"/>
        /// via <c>GetAwaiter().GetResult()</c>.
        /// </summary>
        void Refresh();

        /// <summary>
        /// Asynchronously re-fetches this entity's metadata from the backing store.
        /// </summary>
        Task RefreshAsync(CancellationToken cancellationToken = default);
    }
}
