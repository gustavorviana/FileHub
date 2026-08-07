#nullable enable
using Oci.ObjectstorageService.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.OracleObjectStorage.Internal;

internal interface IOciWorkRequestHandle
{
    string Namespace { get; }
    string Bucket { get; }
    string Region { get; }
    string Id { get; }

    Task CancelAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OciWorkRequestError>> GetErrorsAsync(CancellationToken cancellationToken = default);
    Task<(WorkRequest.StatusEnum Status, float? PercentComplete)> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<WorkRequest.StatusEnum> WaitForTerminalStateAsync(IProgress<float>? progress, CancellationToken cancellationToken = default);
}