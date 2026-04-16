using System;
using FileHub.OracleObjectStorage.Internal;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// Shared fixture used by unit-style tests. Each test class gets its own
/// <see cref="InMemoryOciClient"/> + <see cref="OracleObjectStorageFileHub"/>
/// backed by it — no network I/O, deterministic, fast.
/// </summary>
public sealed class InMemoryOciFixture : IDisposable
{
    internal InMemoryOciClient Client { get; }
    public OracleObjectStorageFileHub FileHub { get; }

    public InMemoryOciFixture()
    {
        Client = new InMemoryOciClient();
        FileHub = OracleObjectStorageFileHub_TestAccess.FromOciClient(Client);
    }

    internal void SetBucketPublic(OciBucketAccessType access) => Client.SetBucketAccess(access);

    public void Dispose()
    {
        FileHub.Dispose();
    }
}

/// <summary>
/// Bridge to the <c>internal</c> <c>FromOciClient</c> factory, reachable by
/// the test assembly through <c>InternalsVisibleTo</c>.
/// </summary>
internal static class OracleObjectStorageFileHub_TestAccess
{
    public static OracleObjectStorageFileHub FromOciClient(IOciClient client, string rootPath = "")
        => OracleObjectStorageFileHub.FromOciClient(client, rootPath);
}
