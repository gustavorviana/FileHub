using System;

namespace FileHub.OracleObjectStorage.Internal
{
    /// <summary>
    /// Everything a committed write needs, computed once from the file's
    /// current metadata snapshot plus the caller's <see cref="FileWriteOptions"/>:
    /// the options sent to the server (user tags plus the driver-internal
    /// last-write bookkeeping tag), the user-facing snapshot to install via
    /// <c>OnWriteCommitted</c>, and the client-side write timestamp both are
    /// stamped with. Shared by the single-<c>PutObject</c> path and the
    /// multipart path so the two can never drift.
    /// </summary>
    internal readonly struct OciWritePayload
    {
        public OciWriteOptions ServerOptions { get; }
        public FileMetadata Snapshot { get; }
        public DateTime TimestampUtc { get; }

        public OciWritePayload(OciWriteOptions serverOptions, FileMetadata snapshot, DateTime timestampUtc)
        {
            ServerOptions = serverOptions;
            Snapshot = snapshot;
            TimestampUtc = timestampUtc;
        }
    }
}
