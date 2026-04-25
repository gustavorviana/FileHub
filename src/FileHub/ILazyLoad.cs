namespace FileHub
{
    /// <summary>
    /// Capability: the file reference may be a "pending stub" whose
    /// cached state (Length, LastModified, Metadata) has not been loaded
    /// from the store yet. Returned by
    /// <c>OpenFile(name, createIfNotExists: true)</c> on S3 and OCI —
    /// those drivers defer every server round-trip until the caller
    /// actually needs the state.
    ///
    /// <para>
    /// On stubs, <see cref="IsLoaded"/> is <c>false</c> and the cached
    /// values are defaults (<c>Length == -1</c>, empty
    /// <see cref="FileMetadata"/>, default timestamps). The flag flips
    /// to <c>true</c> when state is loaded — via
    /// <see cref="IRefreshable.Refresh"/>,
    /// <see cref="FileSystemEntry.Exists"/> (which triggers a Refresh
    /// on hit), or a successful write commit.
    /// </para>
    ///
    /// <para>
    /// Reading bytes (<c>ReadAllBytes</c>, <c>GetReadStream</c>) on a
    /// stub whose backing object doesn't exist throws
    /// <see cref="System.IO.FileNotFoundException"/> at first read.
    /// Writing bytes (<c>SetBytes</c>, streams) creates or overwrites
    /// the object and flips <see cref="IsLoaded"/> to <c>true</c>.
    /// </para>
    /// </summary>
    public interface ILazyLoad : IRefreshable
    {
        /// <summary>
        /// <c>true</c> once the file's state has been loaded from the
        /// store (via <c>Refresh</c>, <c>TryOpenFile</c>, a successful
        /// write, or <c>Exists()</c> returning true on a stub).
        /// <c>false</c> on freshly-opened stubs returned by
        /// <c>OpenFile(name, createIfNotExists: true)</c>.
        /// </summary>
        bool IsLoaded { get; }
    }
}
