using System;
using System.IO;

namespace FileHub.Local
{
    public class LocalFileHub : ILocalFileHub
    {
        public FileDirectory Root { get; }

        public LocalFileHub(string rootPath)
        {
            var resolved = ResolvePath(rootPath);
            Root = new LocalDirectory(resolved, rootPath: resolved, parent: null);
        }

        public LocalFileHub(string rootPath, Func<string, string> pathResolver)
        {
            if (pathResolver == null) throw new ArgumentNullException(nameof(pathResolver));
            var resolved = pathResolver(rootPath);
            Root = new LocalDirectory(resolved, rootPath: resolved, parent: null);
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
