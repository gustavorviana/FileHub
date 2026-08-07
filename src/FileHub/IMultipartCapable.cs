namespace FileHub
{
    /// <summary>
    /// Base capability for files whose backing store supports multipart
    /// upload in any form (backend-streamed or delegated via pre-signed
    /// URLs). Exposes the part-size constraint shared by every multipart
    /// strategy — it belongs to the store, not to the upload flow.
    /// </summary>
    public interface IMultipartCapable
    {
        /// <summary>
        /// Minimum size (in bytes) the backing store accepts for any part
        /// except the last one. Common object-storage backends require
        /// parts of at least 5 MiB.
        /// </summary>
        long MinimumPartSize { get; }
    }
}
