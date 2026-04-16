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
