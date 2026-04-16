namespace FileHub.Memory
{
    /// <summary>
    /// Marker interface for FileHubs that keep all state in process memory.
    /// Exists so consumers can inject a specific driver type through DI
    /// without resolving to the plain <see cref="IFileHub"/> default.
    /// </summary>
    public interface IMemoryFileHub : IFileHub { }
}
