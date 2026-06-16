namespace FileHub
{
    /// <summary>
    /// Describes which optional per-file capabilities a driver supports.
    /// Exposed on <see cref="IFileHub.Features"/>. Consumers branch on these
    /// flags instead of casting to capability interfaces:
    /// <code>
    /// if (hub.Features.Metadata)
    ///     await file.SetBytesAsync(bytes, new FileWriteOptions { ContentType = "image/png" }, ct);
    /// </code>
    /// </summary>
    public sealed class FileHubFeatures
    {
        /// <summary>
        /// <c>true</c> when the driver applies <see cref="FileWriteOptions"/> on writes and
        /// returns populated fields from <see cref="FileEntry.GetMetadataAsync"/>. Drivers without
        /// a native per-object metadata surface report <c>false</c>.
        /// </summary>
        public bool Metadata { get; }

        /// <summary>
        /// <c>true</c> when the driver supports appending to an existing file via
        /// <c>GetWriteStream</c>. Drivers that replace the whole object on write
        /// report <c>false</c>. (Append is not surfaced through the public API yet;
        /// reserved.)
        /// </summary>
        public bool Append { get; }

        public FileHubFeatures(bool metadata = false, bool append = false)
        {
            Metadata = metadata;
            Append = append;
        }

        /// <summary>Shared instance with every capability set to <c>false</c>.</summary>
        public static FileHubFeatures None { get; } = new FileHubFeatures();
    }
}
