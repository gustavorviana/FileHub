using System;
using System.IO;

namespace FileHub.AmazonS3
{
    /// <summary>
    /// Minimal shared base for the driver's file-backed streams. Exists only
    /// for the parent-file plumbing both sides need — the "one stream open
    /// per file" latch subscribes to <see cref="Disposed"/> regardless of
    /// whether the stream reads or writes. Read and write streams share no
    /// I/O logic; anything beyond this event belongs in the concrete class.
    /// </summary>
    internal abstract class S3FileStreamBase : Stream
    {
        public event EventHandler Disposed;

        /// <summary>Fires <see cref="Disposed"/> exactly once per stream lifetime — callers guard with their own disposed flag.</summary>
        protected void RaiseDisposed() => Disposed?.Invoke(this, EventArgs.Empty);
    }
}
