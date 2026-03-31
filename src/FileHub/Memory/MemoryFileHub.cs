namespace FileHub.Memory
{
    public class MemoryFileHub : IFileHub
    {
        public FileDirectory Root { get; }

        public MemoryFileHub(string rootName = "root")
        {
            Root = new MemoryDirectory(rootName);
        }
    }
}
