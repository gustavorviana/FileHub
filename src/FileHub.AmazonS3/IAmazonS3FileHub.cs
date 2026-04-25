namespace FileHub.AmazonS3
{
    /// <summary>
    /// Marker interface for FileHubs backed by AWS S3. Exists so consumers
    /// can inject a specific driver type through DI without resolving to
    /// the plain <see cref="IFileHub"/> default.
    /// </summary>
    public interface IAmazonS3FileHub : IFileHub { }
}
