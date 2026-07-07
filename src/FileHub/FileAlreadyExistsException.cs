using System;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

namespace FileHub
{
    /// <summary>
    /// Thrown by a copy or move that was asked not to overwrite
    /// (<c>overwrite: false</c>) when an entry already exists at the
    /// destination. The source is left untouched — no bytes were written.
    /// </summary>
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
    public sealed class FileAlreadyExistsException : FileHubException
    {
        /// <summary>The destination path that already exists.</summary>
        public string DestinationPath { get; }

        public FileAlreadyExistsException(string destinationPath)
            : base($"An entry already exists at \"{destinationPath}\" and overwrite was not requested.")
        {
            DestinationPath = destinationPath;
        }

        public FileAlreadyExistsException(string message, string destinationPath)
            : base(message)
        {
            DestinationPath = destinationPath;
        }

#if !NET8_0_OR_GREATER
        private FileAlreadyExistsException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            DestinationPath = info.GetString(nameof(DestinationPath));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(DestinationPath), DestinationPath);
        }
#endif
    }
}
