namespace FileHub.Memory
{
    /// <summary>
    /// In-process <see cref="IFileHub"/> backed by a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// hierarchy. Primary use case: unit tests.
    /// <para>
    /// <b>Heads-up on metadata.</b> The memory driver stores
    /// <see cref="FileWriteOptions"/> on each file so tests can round-trip
    /// <c>ContentType</c>, <c>CacheControl</c>, and user tags. The on-disk
    /// <see cref="FileHub.Local.LocalFileHub"/> has no per-object metadata API
    /// and silently drops the same fields. Service code that writes metadata
    /// against this hub may silently lose it when swapped to
    /// <c>LocalFileHub</c> in production — verify per driver rather than
    /// assuming metadata always round-trips.
    /// </para>
    /// </summary>
    public class MemoryFileHub : IMemoryFileHub
    {
        public FileDirectory Root { get; }

        public MemoryFileHub(string rootName = "root")
            : this(rootName, DirectoryPathMode.OpenIntermediates) { }

        /// <summary>
        /// Construct an in-memory hub. <paramref name="pathMode"/> is accepted
        /// for API symmetry with cloud drivers but has no practical effect:
        /// the in-memory driver always opens intermediate directories since
        /// it needs them in its own dictionary hierarchy.
        /// </summary>
        public MemoryFileHub(string rootName, DirectoryPathMode pathMode)
        {
            Root = new MemoryDirectory(rootName, parent: null, pathMode: pathMode);
        }
    }
}
