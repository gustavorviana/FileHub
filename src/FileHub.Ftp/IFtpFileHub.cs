namespace FileHub.Ftp
{
    /// <summary>
    /// Marker interface for FileHubs backed by an FTP server. Exists so
    /// consumers can inject a specific driver type through DI without
    /// resolving to the plain <see cref="IFileHub"/> default.
    /// </summary>
    public interface IFtpFileHub : IFileHub { }
}
