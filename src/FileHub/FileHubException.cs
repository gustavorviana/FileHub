using System;
using System.IO;
#if !NET8_0_OR_GREATER
using System.Runtime.Serialization;
#endif

namespace FileHub
{
    /// <summary>
    /// Base exception for FileHub-specific failures. Inherits from
    /// <see cref="IOException"/> so generic I/O catch handlers still apply.
    /// </summary>
#if !NET8_0_OR_GREATER
    [Serializable]
#endif
    public class FileHubException : IOException
    {
        public FileHubException(string message) : base(message) { }
        public FileHubException(string message, Exception innerException) : base(message, innerException) { }

#if !NET8_0_OR_GREATER
        /// <summary>
        /// Serialization constructor required by <see cref="ISerializable"/>
        /// for round-tripping the exception across AppDomain or remoting
        /// boundaries. Obsolete on .NET 8+ — see SYSLIB0051.
        /// </summary>
        protected FileHubException(SerializationInfo info, StreamingContext context)
            : base(info, context) { }
#endif
    }
}
