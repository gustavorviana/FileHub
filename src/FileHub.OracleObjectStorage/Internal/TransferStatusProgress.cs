#nullable enable

using System;

namespace FileHub.OracleObjectStorage.Internal;

internal sealed class TransferStatusProgress : IProgress<float>
{
    private readonly object _sync = new();
    private readonly long _length;
    private readonly IProgress<TransferStatus>? _target;

    private float? _percentComplete;

    /// <summary>
    /// Gets the last distinct percentage reported by OCI.
    /// </summary>
    public float? PercentComplete
    {
        get
        {
            lock (_sync)
            {
                return _percentComplete;
            }
        }
    }

    private TransferStatusProgress(long length, IProgress<TransferStatus>? target)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "The content length cannot be negative.");
        }

        _length = length;
        _target = target;
    }

    public static TransferStatusProgress? FromCallback(long length, IProgress<TransferStatus>? target)
    {
        if (target == null)
            return null;

        return new TransferStatusProgress(length, target);
    }

    public void Report(float percent)
    {
        if (float.IsNaN(percent) || float.IsInfinity(percent))
            return;

        if (percent < 0f)
            percent = 0f;
        else if (percent > 100f)
            percent = 100f;

        percent = (float)Math.Round(
            percent,
            2,
            MidpointRounding.AwayFromZero);

        lock (_sync)
        {
            if (_percentComplete == percent)
                return;

            _percentComplete = percent;
        }

        _target?.Report(
            TransferStatus.FromPercent(_length, percent));
    }
}