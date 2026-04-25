using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage.Internal
{
    /// <summary>
    /// Runs an async delegate synchronously without risk of deadlock on any
    /// host — including legacy <see cref="SynchronizationContext"/> hosts
    /// (WinForms, WPF, ASP.NET "classic"). The work is always queued to the
    /// thread pool via <see cref="Task.Run(Func{Task})"/>, so the captured
    /// context on the calling thread never participates in the continuation.
    /// </summary>
    internal static class SyncBridge
    {
        public static T Run<T>(Func<CancellationToken, Task<T>> asyncWork, CancellationToken cancellationToken = default)
            => Task.Run(() => asyncWork(cancellationToken), cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

        public static void Run(Func<CancellationToken, Task> asyncWork, CancellationToken cancellationToken = default)
            => Task.Run(() => asyncWork(cancellationToken), cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
    }
}
