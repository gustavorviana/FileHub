namespace FileHub.Local
{
    /// <summary>
    /// Marker interface for FileHubs backed by the local filesystem.
    /// Exists so consumers can inject a specific driver type through DI
    /// without resolving to the plain <see cref="IFileHub"/> default.
    /// </summary>
    public interface ILocalFileHub : IFileHub { }
}
