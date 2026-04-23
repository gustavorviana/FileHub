using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileHub.Ftp.Internal
{
    /// <summary>
    /// Narrow abstraction over the operations the FileHub driver needs from
    /// an FTP server. All driver classes depend only on this interface so the
    /// storage logic can be unit-tested with an in-memory fake, with no
    /// FluentFTP types leaking into the public API.
    /// </summary>
    internal interface IFtpClient : IDisposable
    {
        /// <summary>
        /// Opaque identity shared by clients connected to the same server with
        /// the same credentials. Reference equality of this object decides
        /// whether the driver may issue a server-side <c>RNFR/RNTO</c> across
        /// directories — FTP rename only works inside a single connection.
        /// </summary>
        object ConnectionScope { get; }

        Task ConnectAsync(CancellationToken cancellationToken = default);

        Task<FtpItemInfo> StatAsync(string path, CancellationToken cancellationToken = default);

        Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);

        Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default);

        Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken = default);

        Task<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken = default);

        Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

        Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

        Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken = default);

        Task CreateDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<FtpItemInfo>> ListAsync(string path, CancellationToken cancellationToken = default);
    }

    internal sealed class FtpItemInfo
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
