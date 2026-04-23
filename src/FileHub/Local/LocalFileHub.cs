using System;
using System.IO;

namespace FileHub.Local
{
    public class LocalFileHub : ILocalFileHub
    {
        public FileDirectory Root { get; }

        public LocalFileHub(string rootPath)
            : this(rootPath, DirectoryPathMode.OpenIntermediates) { }

        public LocalFileHub(string rootPath, DirectoryPathMode pathMode)
        {
            var resolved = ResolvePath(rootPath);
            Root = new LocalDirectory(resolved, rootPath: resolved, parent: null, pathMode: pathMode);
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Root path cannot be null or empty.", nameof(path));

            if (path.StartsWith("~"))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path.Substring(1).TrimStart('/', '\\'));

            return Path.GetFullPath(path);
        }
    }
}
