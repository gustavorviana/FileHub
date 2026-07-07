using System;
using System.Linq;

namespace FileHub.OracleObjectStorage.Tests;

internal static class OciEnvironment
{
    public static readonly string[] RequiredVars =
    {
        "FILEHUB_OCI_CONFIG_FILE",
        "FILEHUB_OCI_PROFILE",
        "FILEHUB_OCI_BUCKET",
        "FILEHUB_OCI_NAMESPACE"
    };

    public static string? GetSkipReason()
    {
        var missing = RequiredVars
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();

        return missing.Length == 0
            ? null
            : $"OCI integration tests skipped. Missing env vars: {string.Join(", ", missing)}.";
    }
}

public sealed class RequiresOciAttribute : FactAttribute
{
    public RequiresOciAttribute()
    {
        var reason = OciEnvironment.GetSkipReason();
        if (reason != null) Skip = reason;
    }
}

public sealed class RequiresOciTheoryAttribute : TheoryAttribute
{
    public RequiresOciTheoryAttribute()
    {
        var reason = OciEnvironment.GetSkipReason();
        if (reason != null) Skip = reason;
    }
}

/// <summary>
/// Requires everything <see cref="RequiresOciAttribute"/> needs PLUS
/// <c>FILEHUB_OCI_BUCKET_B</c> — a second bucket in the SAME
/// tenancy/namespace/region, used by cross-bucket copy tests. OCI routes a
/// server-side <c>CopyObject</c> through one authenticated client pinned to a
/// single region, so there is no cross-region counterpart to gate on.
/// </summary>
public sealed class RequiresOciSecondBucketAttribute : FactAttribute
{
    public RequiresOciSecondBucketAttribute()
    {
        var primary = OciEnvironment.GetSkipReason();
        if (primary != null) { Skip = primary; return; }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FILEHUB_OCI_BUCKET_B")))
            Skip = "OCI cross-bucket tests skipped. Missing env var: FILEHUB_OCI_BUCKET_B.";
    }
}
