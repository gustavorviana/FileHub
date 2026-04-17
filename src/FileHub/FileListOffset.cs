using System;

namespace FileHub
{
    public readonly struct FileListOffset : IEquatable<FileListOffset>
    {
        public int Index { get; }
        public string Name { get; }

        public bool IsNamed => Name != null;

        private FileListOffset(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public static FileListOffset FromIndex(int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be non-negative.");
            return new FileListOffset(index, null);
        }

        public static FileListOffset FromName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));
            return new FileListOffset(0, name);
        }

        public static implicit operator FileListOffset(int index) => FromIndex(index);

        public bool Equals(FileListOffset other) => Index == other.Index && Name == other.Name;
        public override bool Equals(object obj) => obj is FileListOffset other && Equals(other);
        public override int GetHashCode() => Name != null ? Name.GetHashCode() : Index.GetHashCode();
        public override string ToString() => IsNamed ? $"Name=\"{Name}\"" : $"Index={Index}";

        public static bool operator ==(FileListOffset left, FileListOffset right) => left.Equals(right);
        public static bool operator !=(FileListOffset left, FileListOffset right) => !left.Equals(right);
    }
}
