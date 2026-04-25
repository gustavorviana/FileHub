namespace FileHub.Tests;

/// <summary>
/// Disposable helper that creates a unique temp directory for a test and
/// recursively deletes it on disposal. Use inside a try/finally.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileHubTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best-effort cleanup; tests should not fail due to leftover files
        }
    }
}
