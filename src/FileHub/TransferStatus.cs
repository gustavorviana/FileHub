namespace FileHub
{
    /// <summary>
    /// Snapshot of an in-flight stream-to-stream transfer. Reported via
    /// <see cref="System.IProgress{T}"/> from
    /// <see cref="FileEntry.CopyToStreamAsync(System.IO.Stream, System.IProgress{TransferStatus}, System.Threading.CancellationToken)"/>
    /// after each buffered chunk is written to the destination.
    /// </summary>
    public readonly struct TransferStatus
    {
        /// <summary>
        /// Bytes written to the destination stream so far. Monotonic; ends
        /// equal to <see cref="TotalBytes"/> when the total is known.
        /// </summary>
        public long BytesTransferred { get; }

        /// <summary>
        /// Total bytes expected, taken from the source file's
        /// <see cref="FileEntry.Length"/> at the start of the transfer.
        /// <c>-1</c> when the driver does not know the length yet (e.g.,
        /// cached <c>-1</c>; call <see cref="IRefreshable.Refresh"/>
        /// beforehand if a total is required).
        /// </summary>
        public long TotalBytes { get; }

        public TransferStatus(long bytesTransferred, long totalBytes)
        {
            BytesTransferred = bytesTransferred;
            TotalBytes = totalBytes;
        }
    }
}
