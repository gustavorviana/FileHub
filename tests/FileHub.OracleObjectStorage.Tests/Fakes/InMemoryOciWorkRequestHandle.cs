#nullable enable

using FileHub.OracleObjectStorage.Internal;
using Oci.ObjectstorageService.Models;

namespace FileHub.OracleObjectStorage.Tests.Fakes;

internal sealed class InMemoryOciWorkRequestHandle
    : IOciWorkRequestHandle
{
    private readonly object _sync = new();
    private readonly List<OciWorkRequestError> _errors = new();

    private WorkRequest.StatusEnum _status;
    private float? _percentComplete;

    private TaskCompletionSource<object?> _stateChanged =
        CreateStateChangedSource();

    private int _cancelInvocationCount;

    public string Namespace { get; }

    public string Bucket { get; }

    public string Region { get; }

    public string Id { get; }

    public int CancelInvocationCount => _cancelInvocationCount;

    public InMemoryOciWorkRequestHandle(
        string id,
        string @namespace,
        string bucket,
        string region,
        WorkRequest.StatusEnum initialStatus =
            WorkRequest.StatusEnum.Completed,
        float? initialPercentComplete = 100)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException(
                "The work request ID cannot be null or empty.",
                nameof(id));
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            throw new ArgumentException(
                "The namespace cannot be null or empty.",
                nameof(@namespace));
        }

        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException(
                "The bucket cannot be null or empty.",
                nameof(bucket));
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            throw new ArgumentException(
                "The region cannot be null or empty.",
                nameof(region));
        }

        ValidatePercentComplete(initialPercentComplete);

        Id = id;
        Namespace = @namespace;
        Bucket = bucket;
        Region = region;

        _status = initialStatus;
        _percentComplete = initialPercentComplete;
    }

    public Task<(WorkRequest.StatusEnum Status, float? PercentComplete)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult(
                (_status, _percentComplete));
        }
    }

    public async Task<WorkRequest.StatusEnum> WaitForTerminalStateAsync(IProgress<float>? progress, CancellationToken cancellationToken = default)
    {
        float? lastReportedProgress = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WorkRequest.StatusEnum status;
            float? percentComplete;
            Task stateChanged;

            lock (_sync)
            {
                status = _status;
                percentComplete = _percentComplete;
                stateChanged = _stateChanged.Task;
            }

            if (percentComplete.HasValue &&
                percentComplete != lastReportedProgress)
            {
                progress?.Report(percentComplete.Value);
                lastReportedProgress = percentComplete;
            }

            if (IsTerminal(status))
                return status;

            await stateChanged
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _cancelInvocationCount);

        lock (_sync)
        {
            if (IsTerminal(_status))
                return Task.CompletedTask;
        }

        SetState(WorkRequest.StatusEnum.Canceled, percentComplete: null);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OciWorkRequestError>> GetErrorsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            IReadOnlyList<OciWorkRequestError> snapshot = [.. _errors];

            return Task.FromResult(snapshot);
        }
    }

    /// <summary>
    /// Updates the simulated state and wakes callers waiting for a terminal
    /// state.
    /// </summary>
    public void SetState(WorkRequest.StatusEnum status, float? percentComplete)
    {
        ValidatePercentComplete(percentComplete);

        TaskCompletionSource<object?> previousStateChanged;

        lock (_sync)
        {
            _status = status;
            _percentComplete = percentComplete;

            previousStateChanged = _stateChanged;
            _stateChanged = CreateStateChangedSource();
        }

        previousStateChanged.TrySetResult(null);
    }

    public void SetErrors(IEnumerable<OciWorkRequestError> errors)
    {
        if (errors is null)
            throw new ArgumentNullException(nameof(errors));

        lock (_sync)
        {
            _errors.Clear();
            _errors.AddRange(errors);
        }
    }

    public void AddError(OciWorkRequestError error)
    {
        if (error is null)
            throw new ArgumentNullException(nameof(error));

        lock (_sync)
        {
            _errors.Add(error);
        }
    }

    private static bool IsTerminal(WorkRequest.StatusEnum status)
    {
        return status == WorkRequest.StatusEnum.Completed
            || status == WorkRequest.StatusEnum.Canceled
            || status == WorkRequest.StatusEnum.Failed;
    }

    private static void ValidatePercentComplete(float? percentComplete)
    {
        if (percentComplete is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentComplete),
                percentComplete,
                "The completion percentage must be between 0 and 100.");
        }
    }

    private static TaskCompletionSource<object?> CreateStateChangedSource()
    {
        return new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}