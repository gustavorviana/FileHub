using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileHub.AmazonS3.Internal
{
    internal static class S3PathUtil
    {
        private static readonly ConcurrentDictionary<string, Regex> _regexCache =
            new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        public static string NormalizePrefix(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return string.Empty;

            var trimmed = path.Replace('\\', '/').TrimStart('/');
            if (trimmed.Length == 0)
                return string.Empty;

            return trimmed.EndsWith("/") ? trimmed : trimmed + "/";
        }

        public static string CombineObjectKey(string parentPrefix, string fileName)
        {
            return (parentPrefix ?? string.Empty) + fileName;
        }

        public static string CombinePrefix(string parentPrefix, string childName)
        {
            return (parentPrefix ?? string.Empty) + childName + "/";
        }

        public static string GetLeafName(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return string.Empty;

            var trimmed = prefix.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            return idx < 0 ? trimmed : trimmed.Substring(idx + 1);
        }

        public static string DisplayPath(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return "/";
            return "/" + prefix.TrimEnd('/');
        }

        public static Regex BuildSearchPatternRegex(string pattern)
        {
            var key = pattern ?? string.Empty;
            return _regexCache.GetOrAdd(key, static k =>
            {
                if (k.Length == 0 || k == "*" || k == "*.*")
                    return new Regex("^.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

                // Glob → regex: `*` matches any sequence, `?` matches a single
                // character. FtpPathUtil follows the same contract; the three
                // drivers must agree so a pattern like "report_?.csv" yields
                // the same result everywhere.
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
        /// Verifies that <paramref name="candidate"/> sits inside
        /// <paramref name="rootPrefix"/>. When <paramref name="rootPrefix"/>
        /// is null/empty the hub is intentionally unconfined and has full
        /// access to every key in the bucket — no check is performed. Callers
        /// that want confinement MUST pass a non-empty <c>rootPath</c> to the
        /// hub factory; an empty string or a whitespace-only value opts the
        /// hub out of this safeguard. A <c>null</c> / empty environment
        /// variable wired straight into the factory will silently disable it.
        /// </summary>
        public static void EnsureWithinRootPrefix(string rootPrefix, string candidate)
        {
            if (string.IsNullOrEmpty(rootPrefix))
                return;
            if (candidate == null || !candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new FileHubException($"Access denied: \"{candidate}\" is outside the root \"{rootPrefix}\".");
        }

        public static string ResolveSafeObjectKey(string rootPrefix, string currentPrefix, string relativeName)
        {
            ValidateName(relativeName);
            var candidate = (currentPrefix ?? string.Empty) + relativeName;
            EnsureWithinRootPrefix(rootPrefix, candidate);
            return candidate;
        }

        public static string ResolveSafeChildPrefix(string rootPrefix, string currentPrefix, string relativeName)
        {
            ValidateName(relativeName);
            var candidate = (currentPrefix ?? string.Empty) + relativeName + "/";
            EnsureWithinRootPrefix(rootPrefix, candidate);
            return candidate;
        }
    }
}
