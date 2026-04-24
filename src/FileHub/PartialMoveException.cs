using System;

namespace FileHub
{
    /// <summary>
    /// Thrown when a cross-target move succeeded in copying the file to the
    /// destination but failed to delete the source. The file now exists in
    /// both places; the source must be removed manually by the caller.
    /// </summary>
    public sealed class PartialMoveException : FileHubException
    {
        public string SourcePath { get; }
        public string DestinationPath { get; }

        public PartialMoveException(string message, string sourcePath, string destinationPath, Exception innerException)
            : base(message, innerException)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
        }
    }
}
