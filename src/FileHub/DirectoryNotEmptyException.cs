using System;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

namespace FileHub
{
    /// <summary>
    /// Thrown by a directory delete that was asked not to recurse
    /// (<c>recursive: false</c>, the default) when the target directory still
    /// contains entries. Mirrors <see cref="System.IO.Directory.Delete(string)"/>,
    /// which refuses to remove a non-empty directory. As a
    /// <see cref="FileHubException"/> it is also an
    /// <see cref="System.IO.IOException"/>, so a generic
    /// <c>catch (IOException)</c> still handles it.
    /// </summary>
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
    public sealed class DirectoryNotEmptyException : FileHubException
    {
        /// <summary>The path of the non-empty directory that was not deleted.</summary>
        public string DirectoryPath { get; }

        public DirectoryNotEmptyException(string directoryPath)
            : base($"The directory \"{directoryPath}\" is not empty; pass recursive: true to delete its contents.")
        {
            DirectoryPath = directoryPath;
        }

        public DirectoryNotEmptyException(string message, string directoryPath)
            : base(message)
        {
            DirectoryPath = directoryPath;
        }

#if !NET8_0_OR_GREATER
        private DirectoryNotEmptyException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            DirectoryPath = info.GetString(nameof(DirectoryPath));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            base.GetObjectData(info, context);
            info.AddValue(nameof(DirectoryPath), DirectoryPath);
        }
#endif
    }
}
