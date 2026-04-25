using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using FluentFTP.Exceptions;

namespace FileHub.Ftp.Internal
{
    /// <summary>
    /// <see cref="IFtpClient"/> implementation backed by FluentFTP's
    /// <see cref="AsyncFtpClient"/>. All FluentFTP-specific exceptions are
    /// translated into BCL / FileHub exceptions inside this class so consumers
    /// only see <see cref="FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="FileHubException"/>.
    /// </summary>
    internal sealed class RealFtpClient : IFtpClient
    {
        // FTP completion code for "550 Requested action not taken: file unavailable".
        // FluentFTP surfaces it through FtpCommandException.CompletionCode.
        private const string NotFoundCode = "550";

        private readonly AsyncFtpClient _client;
        private readonly bool _ownsClient;
        private volatile bool _disposed;

        public object ConnectionScope => _client;

        public bool IsConnected => !_disposed && _client.IsConnected;

        public RealFtpClient(AsyncFtpClient client, bool ownsClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_client.IsConnected) return Task.CompletedTask;
            return TranslateAsync("<connect>", async ct =>
            {
                await _client.Connect(ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<FtpItemInfo> StatAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                var item = await _client.GetObjectInfo(path, true, ct).ConfigureAwait(false);
                return item == null ? null : ToInfo(item);
            }, cancellationToken);
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                return await _client.FileExists(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                return await _client.DirectoryExists(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                return (Stream)await _client.OpenRead(path, FtpDataType.Binary, offset, true, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                return (Stream)await _client.OpenWrite(path, FtpDataType.Binary, false, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                await _client.DeleteFile(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                await _client.DeleteDirectory(path, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(fromPath, async ct =>
            {
                await _client.Rename(fromPath, toPath, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task CreateDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                await _client.CreateDirectory(path, recursive, ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<IReadOnlyList<FtpItemInfo>> ListAsync(string path, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return TranslateAsync(path, async ct =>
            {
                var items = await _client.GetListing(path, ct).ConfigureAwait(false);
                if (items == null || items.Length == 0)
                    return (IReadOnlyList<FtpItemInfo>)Array.Empty<FtpItemInfo>();

                var result = new List<FtpItemInfo>(items.Length);
                foreach (var item in items)
                {
                    if (item == null) continue;
                    if (item.Type != FtpObjectType.File && item.Type != FtpObjectType.Directory) continue;
                    result.Add(ToInfo(item));
                }
                return (IReadOnlyList<FtpItemInfo>)result;
            }, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsClient) _client.Dispose();
        }

        // --- Helpers ---

        private static FtpItemInfo ToInfo(FtpListItem item)
        {
            return new FtpItemInfo
            {
                FullPath = item.FullName,
                Name = item.Name,
                IsDirectory = item.Type == FtpObjectType.Directory,
                Size = item.Size < 0 ? 0 : item.Size,
                ModifiedUtc = item.Modified == DateTime.MinValue ? default : item.Modified.ToUniversalTime(),
                CreatedUtc = item.Created == DateTime.MinValue ? default : item.Created.ToUniversalTime()
            };
        }

        private async Task<T> TranslateAsync<T>(string contextPath, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            catch (FtpException fe)
            {
                throw Translate(fe, contextPath);
            }
        }

        private async Task TranslateAsync(string contextPath, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (FtpException fe)
            {
                throw Translate(fe, contextPath);
            }
        }

        private Exception Translate(FtpException raw, string contextPath)
        {
            if (raw is FtpAuthenticationException auth)
                return new UnauthorizedAccessException(
                    $"FTP authentication failed for \"{contextPath}\": {auth.Message}",
                    auth);

            if (raw is FtpCommandException cmd)
            {
                if (string.Equals(cmd.CompletionCode, NotFoundCode, StringComparison.Ordinal)
                    || MessageIndicatesNotFound(cmd.Message))
                    return new FileNotFoundException(
                        $"FTP path \"{contextPath}\" was not found.",
                        cmd);
            }

            if (MessageIndicatesNotFound(raw.Message))
                return new FileNotFoundException(
                    $"FTP path \"{contextPath}\" was not found.",
                    raw);

            return new FileHubException(
                $"FTP operation failed for \"{contextPath}\": {raw.Message}",
                raw);
        }

        private static bool MessageIndicatesNotFound(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("no such file", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealFtpClient));
        }
    }
}
