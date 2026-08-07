namespace FileHub
{
    /// <summary>
    /// Controls when an automatic write stream switches to multipart and how
    /// much data each multipart request carries. Instances are immutable so a
    /// hub can safely share one configuration across all of its file handles.
    /// Provider-specific drivers validate these values against their backend
    /// and in-memory buffering limits.
    /// </summary>
    public sealed class MultipartStreamOptions
    {
        public const int DefaultThreshold = 32 * 1024 * 1024;
        public const int DefaultPartSize = 64 * 1024 * 1024;

        public static MultipartStreamOptions Default { get; }
            = new MultipartStreamOptions(DefaultThreshold, DefaultPartSize);

        /// <summary>
        /// Bytes buffered by an <see cref="WriteStreamPreference.Auto"/> stream
        /// before it switches from a single-request write to multipart.
        /// </summary>
        public int Threshold { get; }

        /// <summary>
        /// Target multipart part size. Larger parts reduce request count and
        /// extend the size reachable before the backend's part-count limit, at
        /// the cost of more memory per concurrent upload.
        /// </summary>
        public int PartSize { get; }

        public MultipartStreamOptions(int threshold, int partSize)
        {
            Threshold = threshold;
            PartSize = partSize;
        }
    }
}
