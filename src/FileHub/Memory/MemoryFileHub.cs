namespace FileHub.Memory
{
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
