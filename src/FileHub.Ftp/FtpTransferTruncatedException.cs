namespace FileHub.Ftp
{
    /// <summary>
    /// Thrown when an FTP upload completes with a success reply but the server
    /// stored fewer bytes than were sent — a silent tail-truncation that can
    /// occur when the passive data channel lands on stale port state. The
    /// driver auto-retries this for buffered writes (<see cref="FileEntry.SetBytes"/>
    /// / <see cref="FileEntry.SetBytesAsync"/>); for streaming writes
    /// (<see cref="FileEntry.GetWriteStream"/>) the source is consumed once and
    /// cannot be replayed, so the truncation surfaces here for the caller to
    /// handle instead of corrupting the file silently.
    /// </summary>
    public sealed class FtpTransferTruncatedException : FileHubException
    {
        /// <summary>Remote path of the truncated upload.</summary>
        public string Path { get; }

        /// <summary>Number of bytes the driver sent.</summary>
        public long BytesWritten { get; }

        /// <summary>Number of bytes the server reported storing (-1 if unknown).</summary>
        public long BytesStored { get; }

        public FtpTransferTruncatedException(string path, long bytesWritten, long bytesStored)
            : base($"FTP upload of \"{path}\" was truncated: sent {bytesWritten} bytes but the server stored {bytesStored}.")
        {
            Path = path;
            BytesWritten = bytesWritten;
            BytesStored = bytesStored;
        }
    }
}
