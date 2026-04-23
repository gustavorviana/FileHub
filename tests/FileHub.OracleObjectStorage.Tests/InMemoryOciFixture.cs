using System;
using FileHub.OracleObjectStorage.Internal;
using FileHub.OracleObjectStorage.Tests.Fakes;

namespace FileHub.OracleObjectStorage.Tests;

/// <summary>
/// xUnit class fixture: shared across every test in a single test class
/// (used via <c>IClassFixture&lt;InMemoryOciFixture&gt;</c>). Tests scope
/// themselves to a per-test directory under the shared root so state doesn't
/// bleed. Single <see cref="InMemoryOciClient"/> + <see cref="OracleObjectStorageFileHub"/>,
/// no network I/O, deterministic.
/// </summary>
public sealed class InMemoryOciFixture : IDisposable
{
    internal InMemoryOciClient Client { get; }
    public OracleObjectStorageFileHub FileHub { get; }

    public InMemoryOciFixture()
    {
        Client = new InMemoryOciClient();
        FileHub = OracleObjectStorageFileHub.FromOciClient(Client);
    }

    internal void SetBucketPublic(OciBucketAccessType access) => Client.SetBucketAccess(access);

    public void Dispose()
    {
        FileHub.Dispose();
    }
}
