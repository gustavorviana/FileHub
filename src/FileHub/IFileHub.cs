namespace FileHub
{
    public interface IFileHub
    {
        /// <summary>Root directory — the sandbox. Every path resolves under here.</summary>
        DirectoryEntry Root { get; }
    }
}
