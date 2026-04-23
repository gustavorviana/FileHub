using System;
using FileHub.Ftp.Tests.Fakes;

namespace FileHub.Ftp.Tests;

/// <summary>
/// Base class for FTP driver unit tests that need an in-memory backing
/// server. xUnit instantiates the test class once per <c>[Fact]</c>, so the
/// constructor effectively acts as per-test setup and <see cref="Dispose"/>
/// as per-test teardown — the idiomatic xUnit pattern for tests that need
/// isolated state. Tests that need multiple hubs or a non-default root /
/// path mode construct their own instances locally instead of inheriting.
/// </summary>
public abstract class FtpTestBase : IDisposable
{
    internal InMemoryFtpClient Client { get; }
    protected FtpFileHub Hub { get; }

    protected FtpTestBase()
    {
        Client = new InMemoryFtpClient();
        Hub = FtpFileHubTestAccess.FromFtpClient(Client);
    }

    protected FileDirectory Root => Hub.Root;

    public void Dispose()
    {
        Hub.Dispose();
        GC.SuppressFinalize(this);
    }
}
