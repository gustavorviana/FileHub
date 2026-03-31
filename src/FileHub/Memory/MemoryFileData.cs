using System;
using System.IO;

namespace FileHub.Memory
{
    internal class MemoryFileData
    {
        public string Name { get; set; }
        public MemoryStream Stream { get; }
        public DateTime CreationTimeUtc { get; }
        public DateTime LastWriteTimeUtc { get; set; }

        public MemoryFileData(string name)
        {
            Name = name;
            Stream = new MemoryStream();
            CreationTimeUtc = DateTime.UtcNow;
            LastWriteTimeUtc = CreationTimeUtc;
        }

        public MemoryFileData(string name, byte[] content)
        {
            Name = name;
            Stream = new MemoryStream();
            Stream.Write(content, 0, content.Length);
            Stream.Position = 0;
            CreationTimeUtc = DateTime.UtcNow;
            LastWriteTimeUtc = CreationTimeUtc;
        }

        public MemoryFileData Clone(string newName = null)
        {
            var data = new MemoryFileData(newName ?? Name);
            Stream.Position = 0;
            Stream.CopyTo(data.Stream);
            data.Stream.Position = 0;
            Stream.Position = 0;
            return data;
        }
    }
}
