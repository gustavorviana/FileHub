using System;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

namespace FileHub.Ftp
{
    /// <summary>
    /// Thrown when an FTP operation fails with an error that does not map to a
    /// BCL-friendly exception (<see cref="System.IO.FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/>, etc.). Carries the FTP
    /// completion code (e.g. <c>"550"</c>) and the remote path for diagnostics;
    /// the raw FluentFTP exception is available through
    /// <see cref="Exception.InnerException"/> but its type is not part of the
    /// public contract. Mirrors <c>AmazonS3DriverException</c> /
    /// <c>OracleObjectStorageDriverException</c> so every provider surfaces a
    /// dedicated driver-level exception.
    /// </summary>
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
    public sealed class FtpDriverException : FileHubException
    {
        /// <summary>FTP completion code reported by the server (null when unknown).</summary>
        public string CompletionCode { get; }

        /// <summary>Remote path the failing operation targeted (null when not path-scoped).</summary>
        public string Path { get; }

        public FtpDriverException(string message, string completionCode, string path, Exception innerException)
            : base(message, innerException)
        {
            CompletionCode = completionCode;
            Path = path;
        }

#if !NET8_0_OR_GREATER
        private FtpDriverException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            CompletionCode = info.GetString(nameof(CompletionCode));
            Path = info.GetString(nameof(Path));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(CompletionCode), CompletionCode);
            info.AddValue(nameof(Path), Path);
        }
#endif
    }
}
