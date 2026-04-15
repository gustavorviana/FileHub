using System;
using System.IO;

namespace FileHub.Memory
{
    internal class MemoryFileData
    {
        private readonly object _lock = new object();
        private int _activeReaders;
        private bool _activeWriter;

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

        public void AcquireRead()
        {
            lock (_lock)
            {
                if (_activeWriter)
                    throw new FileHubException($"Cannot read file \"{Name}\": a writer is currently active.");
                _activeReaders++;
            }
        }

        public void ReleaseRead()
        {
            lock (_lock)
            {
                if (_activeReaders > 0)
                    _activeReaders--;
            }
        }

        public void AcquireWrite()
        {
            lock (_lock)
            {
                if (_activeWriter)
                    throw new FileHubException($"Cannot write file \"{Name}\": another writer is already active.");
                if (_activeReaders > 0)
                    throw new FileHubException($"Cannot write file \"{Name}\": one or more readers are currently active.");
                _activeWriter = true;
            }
        }

        public void ReleaseWrite()
        {
            lock (_lock)
            {
                _activeWriter = false;
            }
        }

        public MemoryFileData Clone(string newName = null)
        {
            var data = new MemoryFileData(newName ?? Name);
            var savedPosition = Stream.Position;
            Stream.Position = 0;
            Stream.CopyTo(data.Stream);
            Stream.Position = savedPosition;
            data.Stream.Position = 0;
            return data;
        }
    }
}
