using System.Threading.Tasks;
using FileHub.Ftp.Internal;

namespace FileHub.Ftp.Tests;

/// <summary>
/// Bridge to the <c>internal</c> <c>FromFtpClient</c> / <c>FromFtpClientAsync</c>
/// factories, reachable by the test assembly through <c>InternalsVisibleTo</c>.
/// Kept separate from any test fixture so construction concerns don't creep
/// into test-class lifecycle.
/// </summary>
internal static class FtpFileHubTestAccess
{
    public static FtpFileHub FromFtpClient(IFtpClient client, string rootPath = "/")
        => FtpFileHub.FromFtpClient(client, rootPath);

    public static FtpFileHub FromFtpClient(IFtpClient client, string rootPath, DirectoryPathMode pathMode)
        => FtpFileHub.FromFtpClient(client, rootPath, pathMode);

    public static Task<FtpFileHub> FromFtpClientAsync(IFtpClient client, string rootPath = "/")
        => FtpFileHub.FromFtpClientAsync(client, rootPath);
}
