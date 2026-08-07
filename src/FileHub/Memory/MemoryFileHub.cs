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
        public DirectoryEntry Root { get; }

        public MemoryFileHub(string rootName = "root")
        {
            Root = new MemoryDirectory(rootName, parent: null);
        }
    }
}
