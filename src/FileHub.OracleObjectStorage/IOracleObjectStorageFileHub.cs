namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// Marker interface for FileHubs backed by Oracle Cloud Infrastructure
    /// Object Storage. Exists so consumers can inject a specific driver type
    /// through DI without resolving to the plain <see cref="IFileHub"/> default.
    /// </summary>
    public interface IOracleObjectStorageFileHub : IFileHub { }
}
