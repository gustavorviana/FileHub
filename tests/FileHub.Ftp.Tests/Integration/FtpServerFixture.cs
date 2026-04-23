using System;
using System.Threading.Tasks;
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
    private const int ControlHostPort = 22121;
    private const int PasvMin = 22130;
    private const int PasvMax = 22139;

    public const string User = "testuser";
    public const string Password = "testpass";

    public string Host => "localhost";
    public int Port => ControlHostPort;

    public string? SkipReason { get; private set; }

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

            // vsftpd comes up a beat after the container is marked running.
            // Poll the control port via FluentFTP until it accepts a login.
            await WaitForFtpReadyAsync(timeout: TimeSpan.FromSeconds(30));
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
        if (_container != null)
            await _container.DisposeAsync();
    }
}
