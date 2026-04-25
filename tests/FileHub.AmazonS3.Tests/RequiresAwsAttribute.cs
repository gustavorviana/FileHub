using System;
using System.Linq;

namespace FileHub.AmazonS3.Tests;

internal static class AwsEnvironment
{
    public static readonly string[] RequiredVars =
    {
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_REGION",
        "FILEHUB_S3_BUCKET"
    };

    public static string? GetSkipReason()
    {
        var missing = RequiredVars
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();

        return missing.Length == 0
            ? null
            : $"AWS integration tests skipped. Missing env vars: {string.Join(", ", missing)}.";
    }
}

public sealed class RequiresAwsAttribute : FactAttribute
{
    public RequiresAwsAttribute()
    {
        var reason = AwsEnvironment.GetSkipReason();
        if (reason != null) Skip = reason;
    }
}

/// <summary>
/// Requires everything <see cref="RequiresAwsAttribute"/> needs PLUS
/// <c>FILEHUB_S3_BUCKET_B</c> + <c>AWS_REGION_B</c> — a second bucket
/// (same or different region) used by cross-target integration tests.
/// </summary>
public sealed class RequiresAwsSecondBucketAttribute : FactAttribute
{
    public RequiresAwsSecondBucketAttribute()
    {
        var primary = AwsEnvironment.GetSkipReason();
        if (primary != null) { Skip = primary; return; }

        var missing = new[] { "FILEHUB_S3_BUCKET_B", "AWS_REGION_B" }
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();
        if (missing.Length > 0)
            Skip = $"AWS secondary-bucket tests skipped. Missing env vars: {string.Join(", ", missing)}.";
    }
}

/// <summary>
/// Like <see cref="RequiresAwsSecondBucketAttribute"/> but additionally
/// requires the two buckets to be in <b>different</b> regions — the only
/// configuration that actually exercises cross-region CopyObject
/// routing. Skipped when <c>AWS_REGION_B == AWS_REGION</c>.
/// </summary>
public sealed class RequiresAwsCrossRegionAttribute : FactAttribute
{
    public RequiresAwsCrossRegionAttribute()
    {
        var primary = AwsEnvironment.GetSkipReason();
        if (primary != null) { Skip = primary; return; }

        var missing = new[] { "FILEHUB_S3_BUCKET_B", "AWS_REGION_B" }
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();
        if (missing.Length > 0) { Skip = $"AWS secondary-bucket tests skipped. Missing env vars: {string.Join(", ", missing)}."; return; }

        var regionA = Environment.GetEnvironmentVariable("AWS_REGION")!;
        var regionB = Environment.GetEnvironmentVariable("AWS_REGION_B")!;
        if (string.Equals(regionA, regionB, StringComparison.Ordinal))
            Skip = $"Cross-region integration test skipped: AWS_REGION and AWS_REGION_B are both \"{regionA}\". Set AWS_REGION_B to a different region to exercise cross-region CopyObject.";
    }
}
