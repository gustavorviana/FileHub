namespace FileHub
{
    public interface IFileHub
    {
        /// <summary>Root directory — the sandbox. Every path resolves under here.</summary>
        FileDirectory Root { get; }

        /// <summary>
        /// Optional per-file capabilities the driver supports (metadata, append, …).
        /// Drivers without these features return <see cref="FileHubFeatures.None"/>.
        /// </summary>
        FileHubFeatures Features { get; }
    }
}
