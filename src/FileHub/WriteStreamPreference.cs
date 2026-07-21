namespace FileHub
{
    /// <summary>
    /// Caller preference for how a write stream commits its bytes, passed to
    /// <see cref="FileEntry.GetWriteStream(FileWriteOptions, WriteStreamPreference)"/> /
    /// <see cref="FileEntry.GetWriteStreamAsync(FileWriteOptions, WriteStreamPreference, System.Threading.CancellationToken)"/>.
    /// Drivers without a multipart surface (Local, Memory, FTP) ignore the
    /// preference silently — same contract as <see cref="FileWriteOptions"/>
    /// fields: unsupported means ignored, never thrown.
    /// </summary>
    public enum WriteStreamPreference
    {
        /// <summary>
        /// Driver decides (default). Multipart-capable drivers buffer small
        /// payloads for a single-request commit and transparently switch to a
        /// multipart upload once the payload outgrows the part-size threshold.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Never switch to multipart: buffer the full payload in memory and
        /// commit it in a single request. The caller takes on the memory cost
        /// of the whole payload — use only when the payload is known small.
        /// </summary>
        Single = 1,

        /// <summary>
        /// Start as a multipart upload from the first written byte, skipping
        /// the single-request buffering phase. Use when the payload is known
        /// large. Ignored (falls back to <see cref="Auto"/> behavior) on
        /// drivers without multipart support.
        /// </summary>
        Multipart = 2
    }
}
