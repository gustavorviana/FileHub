namespace FileHub
{
    /// <summary>
    /// Controls how a <see cref="FileDirectory"/> resolves nested paths
    /// (<c>"a/b/c"</c>) passed to <see cref="FileDirectory.CreateDirectory(string)"/>
    /// and <see cref="FileDirectory.TryOpenDirectory(string, out FileDirectory)"/>.
    /// Configured on the hub; propagated to every directory instance.
    /// </summary>
    public enum DirectoryPathMode
    {
        /// <summary>
        /// Resolve each intermediate segment individually (open-or-create per
        /// segment). Safer — verifies each parent and creates directory
        /// objects that reflect the full tree. Default for in-memory and
        /// local filesystem hubs where traversal is cheap.
        /// </summary>
        OpenIntermediates,

        /// <summary>
        /// Skip intermediate probing and resolve/create the target in the
        /// smallest number of operations the backend supports: one recursive
        /// <c>mkdir</c> on a local filesystem, one <c>PUT</c>/<c>HEAD</c> on
        /// object storage. Cost-optimised — default for cloud-storage hubs
        /// (OCI Object Storage, AWS S3) where each API call is billed.
        /// Drivers that cannot usefully skip intermediates (e.g. the in-memory
        /// driver) fall back to <see cref="OpenIntermediates"/> behaviour.
        /// </summary>
        Direct
    }
}
