using System.Collections.Concurrent;

namespace FileHub.Tests;

/// <summary>
/// Proves that <see cref="SyncBridge"/> stops sync-over-async code from
/// deadlocking under a <see cref="SynchronizationContext"/> that serialises
/// continuations on a single thread (mimics WinForms / WPF / ASP.NET classic).
/// Each test deliberately uses an <c>await</c> without
/// <c>ConfigureAwait(false)</c> so the inner Task would try to resume on the
/// captured context — if <see cref="SyncBridge"/> ever stops pushing the work
/// through <see cref="Task.Run(Func{Task})"/>, these tests deadlock and the
/// 10-second watchdog fails them. The bridge lives in the FileHub core
/// assembly and is shared by every driver, so its contract is exercised here
/// rather than in a driver-specific test project.
/// </summary>
public class SyncBridgeDeadlockTests
{
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Run_Void_UnderSingleThreadContext_CompletesWithoutDeadlock()
    {
        using var ctx = new SingleThreadSyncContext();

        var error = ctx.Run(() =>
        {
            SyncBridge.Run(async ct =>
            {
                // No ConfigureAwait(false): would re-post to the single
                // thread and deadlock if SyncBridge forwarded the await
                // in-context. SyncBridge.Run hops to the thread pool via
                // Task.Run, so the captured context is bypassed.
                await Task.Delay(10, ct);
            });
        });

        Assert.Null(error);
    }

    [Fact]
    public void Run_Result_UnderSingleThreadContext_ReturnsValueWithoutDeadlock()
    {
        using var ctx = new SingleThreadSyncContext();

        int result = 0;
        var error = ctx.Run(() =>
        {
            result = SyncBridge.Run(async ct =>
            {
                await Task.Delay(10, ct);
                return 42;
            });
        });

        Assert.Null(error);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Run_PropagatesInnerException_WithoutWrappingInAggregate()
    {
        using var ctx = new SingleThreadSyncContext();

        var error = ctx.Run(() =>
        {
            SyncBridge.Run(async ct =>
            {
                await Task.Delay(5, ct);
                throw new InvalidOperationException("boom");
            });
        });

        Assert.NotNull(error);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("boom", error!.Message);
    }

    /// <summary>
    /// Serialises posted continuations on one dedicated thread — the minimal
    /// shape of a WinForms / WPF UI thread. <see cref="Run(Action)"/> marshals
    /// the action onto that thread and returns the captured exception (if
    /// any). A <see cref="TimeoutException"/> is returned when the action
    /// doesn't finish within <see cref="DeadlockTimeout"/>, which is how a
    /// real deadlock would manifest.
    /// </summary>
    internal sealed class SingleThreadSyncContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _worker;

        public SingleThreadSyncContext()
        {
            _worker = new Thread(Pump)
            {
                IsBackground = true,
                Name = nameof(SingleThreadSyncContext)
            };
            _worker.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Exception? Run(Action action)
        {
            using var done = new ManualResetEventSlim();
            Exception? captured = null;

            Post(_ =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            }, null);

            if (!done.Wait(DeadlockTimeout))
                return new TimeoutException(
                    $"Deadlock: action did not complete within {DeadlockTimeout.TotalSeconds:N0}s.");

            return captured;
        }

        private void Pump()
        {
            SetSynchronizationContext(this);
            foreach (var item in _queue.GetConsumingEnumerable())
                item.Callback(item.State);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _worker.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
