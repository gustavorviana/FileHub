using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentFTP;

namespace FileHub.Ftp.Tests.Integration;

/// <summary>
/// xUnit class fixture that spins up a real FTP server in a Docker container
/// for the lifetime of the test class, then tears it down. Uses
/// <c>delfer/alpine-ftp-server</c> — vsftpd with passive mode wired up via
/// env vars. Fixed host ports so passive-mode negotiation (<c>PASV</c>)
/// lines up: the container advertises the same port number the client then
/// connects to on the host.
/// </summary>
public sealed class FtpServerFixture : IAsyncLifetime
{
    private const string Image = "stilliard/pure-ftpd:latest";

    // Picked outside the typical dev FTP / passive range to avoid clashes.
    // A wide passive range matters: the suite opens a fresh control + data
    // connection per test in quick succession, and a narrow range gets
    // recycled before the OS clears TIME_WAIT — the longest-lived transfer
    // (the multi-MB round-trip) then lands on a stale data port and truncates.
    private const int ControlHostPort = 22121;
    private const int PasvMin = 22130;
    private const int PasvMax = 22169;

    public const string User = "testuser";
    public const string Password = "testpass";

    public string Host => "localhost";
    public int Port => ControlHostPort;

    public string? SkipReason { get; private set; }

    /// <summary>
    /// A single hub / control connection shared by the whole integration
    /// class. Reusing one connection is both closer to real usage (a hub is
    /// long-lived) and avoids the passive-data-channel races that rapid
    /// per-test connect/disconnect cycling triggers against one container.
    /// Tests isolate themselves via per-test subdirectories, not per-test
    /// connections. <c>null</c> when Docker is unavailable.
    /// </summary>
    public FtpFileHub? Hub { get; private set; }

    private IContainer? _container;

    public async Task InitializeAsync()
    {
        if (DockerEnvironment.GetSkipReason() is { } reason)
        {
            SkipReason = reason;
            return;
        }

        try
        {
            var builder = new ContainerBuilder(Image)
                .WithEnvironment("PUBLICHOST", "localhost")
                .WithEnvironment("FTP_USER_NAME", User)
                .WithEnvironment("FTP_USER_PASS", Password)
                .WithEnvironment("FTP_USER_HOME", $"/home/{User}")
                .WithEnvironment("FTP_USER_UID", "1000")
                .WithEnvironment("FTP_USER_GID", "1000")
                // pure-ftpd defaults to 5 clients / 5 connections-per-IP. The
                // suite opens a fresh connection per test and lingering ones
                // sit in TIME_WAIT, so the cap is hit under load and transfers
                // truncate. Raise it well above the test count.
                .WithEnvironment("FTP_MAX_CLIENTS", "50")
                .WithEnvironment("FTP_MAX_CONNECTIONS", "50")
                .WithEnvironment("FTP_PASSIVE_PORTS", $"{PasvMin}:{PasvMax}")
                // Control channel: host 22121 → container 21.
                .WithPortBinding(ControlHostPort, 21);

            // Passive data channels: host N → container N (fixed 1:1 so the
            // address the server sends in PASV responses is reachable from the
            // client on localhost).
            for (int p = PasvMin; p <= PasvMax; p++)
                builder = builder.WithPortBinding(p, p);

            _container = builder.Build();
            await _container.StartAsync();

            // pure-ftpd comes up a beat after the container is marked running
            // (user creation + daemon start), and the first FluentFTP login
            // can take several seconds on a cold Docker Desktop. 30s proved
            // too tight on dev machines — keep a generous window; the poll
            // returns as soon as a login succeeds.
            await WaitForFtpReadyAsync(timeout: TimeSpan.FromSeconds(90));

            Hub = await FtpFileHub.ConnectAsync(Host, Port, User, Password, rootPath: "/");
        }
        catch (Exception ex)
        {
            SkipReason = $"Failed to start FTP container: {ex.Message}";
            if (_container != null)
            {
                try { await _container.DisposeAsync(); } catch { /* swallow */ }
                _container = null;
            }
        }
    }

    private async Task WaitForFtpReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new AsyncFtpClient(Host, User, Password, Port);
                await probe.Connect();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(250);
            }
        }
        throw new TimeoutException(
            $"FTP server didn't accept a login within {timeout.TotalSeconds:F0}s." +
            (last is null ? "" : $" Last error: {last.Message}"));
    }

    public async Task DisposeAsync()
    {
        Hub?.Dispose();
        if (_container != null)
            await _container.DisposeAsync();
    }
}
