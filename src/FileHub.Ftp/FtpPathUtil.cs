using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileHub.Ftp
{
    internal static class FtpPathUtil
    {
        private static readonly ConcurrentDictionary<string, Regex> _regexCache =
            new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>
        /// Normalises a caller-supplied root path to the FTP convention used
        /// internally: an absolute, forward-slash path with no trailing slash
        /// (or the literal "/" for the server root).
        /// </summary>
        public static string NormalizeRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";

            var unified = path.Replace('\\', '/').Trim();
            if (unified.Length == 0) return "/";

            if (unified[0] != '/') unified = "/" + unified;
            if (unified.Length > 1 && unified.EndsWith("/", StringComparison.Ordinal))
                unified = unified.TrimEnd('/');
            return unified.Length == 0 ? "/" : unified;
        }

        /// <summary>
        /// Combines a parent FTP directory path with a single child segment,
        /// producing an absolute path with single forward slashes.
        /// </summary>
        public static string Combine(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || parent == "/")
                return "/" + child;
            return parent + "/" + child;
        }

        public static string GetLeafName(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return string.Empty;
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            return idx < 0 ? trimmed : trimmed.Substring(idx + 1);
        }

        public static string GetParent(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return "/";
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            if (idx <= 0) return "/";
            return trimmed.Substring(0, idx);
        }

        public static Regex BuildSearchPatternRegex(string pattern)
        {
            var key = pattern ?? string.Empty;
            return _regexCache.GetOrAdd(key, static k =>
            {
                if (k.Length == 0 || k == "*" || k == "*.*")
                    return new Regex("^.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

                var escaped = Regex.Escape(k).Replace("\\*", ".*").Replace("\\?", ".");
                return new Regex("^" + escaped + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            });
        }

        public static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));
            if (name == "." || name == "..")
                throw new ArgumentException($"Name \"{name}\" is not allowed.", nameof(name));
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                throw new ArgumentException($"Name \"{name}\" must not contain path separators.", nameof(name));
            if (name.Any(c => char.IsControl(c)))
                throw new ArgumentException($"Name \"{name}\" contains control characters.", nameof(name));
        }

        /// <summary>
        /// Verifies that <paramref name="candidate"/> stays within the FileHub
        /// sandbox rooted at <paramref name="rootPath"/>. The hub root is
        /// always treated as inclusive.
        /// </summary>
        public static void EnsureWithinRoot(string rootPath, string candidate)
        {
            if (string.IsNullOrEmpty(rootPath) || rootPath == "/") return;
            if (candidate == null)
                throw new FileHubException($"Access denied: null path is outside the root \"{rootPath}\".");

            var normalizedCandidate = candidate.TrimEnd('/');
            if (string.Equals(normalizedCandidate, rootPath, StringComparison.Ordinal)) return;

            var rootWithSep = rootPath.EndsWith("/", StringComparison.Ordinal) ? rootPath : rootPath + "/";
            if (!normalizedCandidate.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new FileHubException($"Access denied: \"{candidate}\" is outside the root \"{rootPath}\".");
        }

        public static string ResolveSafeChildPath(string rootPath, string currentPath, string relativeName)
        {
            ValidateName(relativeName);
            var candidate = Combine(currentPath, relativeName);
            EnsureWithinRoot(rootPath, candidate);
            return candidate;
        }
    }
}
