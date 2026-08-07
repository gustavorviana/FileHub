#nullable enable
using Oci.ObjectstorageService.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage.Internal;

internal static class OciWorkRequestHandleExtensions
{
    public static async Task<WorkRequest.StatusEnum> WaitAndRequestCancellationAsync(this IOciWorkRequestHandle operationHandle, IProgress<float>? progress, CancellationToken cancellationToken = default)
    {
        try
        {
            return await operationHandle.WaitForTerminalStateAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await operationHandle.CancelAsync(cancelTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort: preserve the original local cancellation.
            }

            throw;
        }
    }
}