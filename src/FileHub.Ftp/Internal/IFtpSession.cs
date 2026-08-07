using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Ftp.Internal
{
    /// <summary>
    /// Per-FileHub shared state. Owns the <see cref="IFtpClient"/> and gates
    /// the lazy connect on first use so concurrent operations don't all race
    /// to <c>CONNECT</c>.
    /// </summary>
    internal interface IFtpSession : IDisposable
    {
        IFtpClient Client { get; }

        Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
    }
}
