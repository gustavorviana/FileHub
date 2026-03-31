using System;
using System.IO;

namespace FileHub
{
    public class FileHubException : IOException
    {
        public FileHubException(string message) : base(message) { }
        public FileHubException(string message, Exception innerException) : base(message, innerException) { }
    }
}
