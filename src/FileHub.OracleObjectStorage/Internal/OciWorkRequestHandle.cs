#nullable enable

using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Requests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OciStatusEnum = Oci.ObjectstorageService.Models.WorkRequest.StatusEnum;

namespace FileHub.OracleObjectStorage.Internal;

/// <summary>
/// Represents a reference to an existing OCI work request.
///
/// This class does not own the underlying ObjectStorageClient and must not
/// dispose it.
/// </summary>
internal sealed class OciWorkRequestHandle : IOciWorkRequestHandle
{
    private static readonly TimeSpan InitialPollingDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumPollingDelay = TimeSpan.FromSeconds(2);
    private readonly ObjectStorageClient _client;

    public string Namespace { get; }
    public string Bucket { get; }
    public string Region { get; }
    public string Id { get; }

    public OciWorkRequestHandle(ObjectStorageClient client, string workRequestId, string @namespace, string bucket, string region)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        if (string.IsNullOrWhiteSpace(workRequestId))
            throw new ArgumentException("The work request ID cannot be null or empty.", nameof(workRequestId));

        if (string.IsNullOrWhiteSpace(@namespace))
            throw new ArgumentException("The namespace cannot be null or empty.", nameof(@namespace));

        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("The bucket cannot be null or empty.", nameof(bucket));

        if (string.IsNullOrWhiteSpace(region))
            throw new ArgumentException("The region cannot be null or empty.", nameof(region));

        Id = workRequestId;
        Namespace = @namespace;
        Bucket = bucket;
        Region = region;
    }

    /// <summary>
    /// Gets the current state of the remote work request.
    /// </summary>
    public async Task<(OciStatusEnum Status, float? PercentComplete)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetWorkRequest(new GetWorkRequestRequest { WorkRequestId = Id },
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);

            return (response.WorkRequest.Status ?? OciStatusEnum.UnknownEnumValue, response.WorkRequest.PercentComplete);
        }
        catch (Exception ex) when (OciExceptionTranslator.ShouldTranslate(ex))
        {
            throw OciExceptionTranslator.TranslateWorkRequest(ex, Id, Namespace, Bucket, Region);
        }
    }
    /// <summary>
    /// Waits until the work request reaches a terminal state.
    ///
    /// Canceling the supplied token stops only the local wait. It does not
    /// request cancellation of the remote OCI operation.
    /// </summary>
    public async Task<OciStatusEnum> WaitForTerminalStateAsync(IProgress<float>? progress, CancellationToken cancellationToken = default)
    {
        var delay = InitialPollingDelay;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (status, percentComplete) = await GetStatusAsync(cancellationToken).ConfigureAwait(false);

            if (status == OciStatusEnum.UnknownEnumValue)
                throw new InvalidOperationException($"OCI work request \"{Id}\" returned an unknown status.");

            var hasProgressToReport = progress != null && percentComplete.HasValue;
            if (hasProgressToReport)
                progress!.Report(percentComplete!.Value / 100);

            if (IsTerminal(status))
                return status;

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (!hasProgressToReport && delay < MaximumPollingDelay)
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumPollingDelay.Ticks));
        }
    }

    /// <summary>
    /// Requests cancellation of the remote OCI operation.
    ///
    /// Completion of this method means that OCI accepted the cancellation
    /// request. It does not mean that the work request is already canceled.
    /// Use WaitForTerminalStateAsync to observe the final state.
    /// </summary>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.CancelWorkRequest(new CancelWorkRequestRequest { WorkRequestId = Id },
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception ex) when (OciExceptionTranslator.ShouldTranslate(ex))
        {
            throw OciExceptionTranslator.TranslateWorkRequest(ex, Id, Namespace, Bucket, Region);
        }
    }

    /// <summary>
    /// Gets the errors reported for the work request.
    /// </summary>
    public async Task<IReadOnlyList<OciWorkRequestError>> GetErrorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.ListWorkRequestErrors(
                new ListWorkRequestErrorsRequest { WorkRequestId = Id },
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);

            if (response.Items == null || response.Items.Count == 0)
                return [];

            return [.. response.Items.Select(item => new OciWorkRequestError(item.Code, item.Message))];
        }
        catch (Exception ex) when (OciExceptionTranslator.ShouldTranslate(ex))
        {
            throw OciExceptionTranslator.TranslateWorkRequest(ex, Id, Namespace, Bucket, Region);
        }
    }

    private static bool IsTerminal(OciStatusEnum status)
    {
        return status == OciStatusEnum.Completed
            || status == OciStatusEnum.Canceled
            || status == OciStatusEnum.Failed;
    }
}

internal sealed class OciWorkRequestError(string code, string message)
{
    public string Code { get; } = code;

    public string Message { get; } = message;

    public override string ToString()
    {
        return string.IsNullOrEmpty(Code)
            ? Message
            : $"[{Code}] {Message}";
    }
}