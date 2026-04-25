using System;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

namespace FileHub
{
    /// <summary>
    /// Thrown when a cross-target move succeeded in copying the file to the
    /// destination but failed to delete the source. The file now exists in
    /// both places; the source must be removed manually by the caller.
    /// </summary>
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
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

#if !NET8_0_OR_GREATER
        private PartialMoveException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            SourcePath = info.GetString(nameof(SourcePath));
            DestinationPath = info.GetString(nameof(DestinationPath));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(SourcePath), SourcePath);
            info.AddValue(nameof(DestinationPath), DestinationPath);
        }
#endif
    }
}
