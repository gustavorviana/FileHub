using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;

namespace FileHub.Ftp.Tests.Integration;

/// <summary>
/// One-shot probe that checks whether a Docker daemon is reachable via the
/// environment Testcontainers would use. Cached — probing costs a round-trip
/// and the availability state doesn't change mid-test-run.
/// </summary>
internal static class DockerEnvironment
{
    private static readonly Lazy<string?> _skipReason = new(Probe, isThreadSafe: true);

    public static string? GetSkipReason() => _skipReason.Value;

    private static string? Probe()
    {
        try
        {
            // Building and immediately disposing a container only talks to the
            // daemon to negotiate the session — it doesn't start anything.
            // If Docker isn't reachable, Testcontainers throws synchronously.
            var probe = new ContainerBuilder("hello-world")
                .Build();
            probe.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return $"Docker not reachable — skipping FTP integration tests. ({ex.Message})";
        }
    }
}
