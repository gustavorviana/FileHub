namespace FileHub.Ftp.Tests.Integration;

/// <summary>
/// xUnit <c>[Fact]</c> that skips when no Docker daemon is reachable — so
/// developers without Docker, and CI jobs that don't need Docker, never
/// fail on these tests. Skip reason is probed once and cached.
/// </summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        var reason = DockerEnvironment.GetSkipReason();
        if (reason != null) Skip = reason;
    }
}
