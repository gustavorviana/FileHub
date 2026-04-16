using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileHub.OracleObjectStorage.Internal
{
    internal static class OciPathUtil
    {
        public static string NormalizePrefix(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return string.Empty;

            var trimmed = path.Replace('\\', '/').TrimStart('/');
            if (trimmed.Length == 0)
                return string.Empty;

            return trimmed.EndsWith("/") ? trimmed : trimmed + "/";
        }

        public static string CombineObjectName(string parentPrefix, string fileName)
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
            if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*")
                return new Regex("^.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
            return new Regex("^" + escaped + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

        public static void EnsureWithinRootPrefix(string rootPrefix, string candidate)
        {
            if (string.IsNullOrEmpty(rootPrefix))
                return;
            if (candidate == null || !candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new FileHubException($"Access denied: \"{candidate}\" is outside the root \"{rootPrefix}\".");
        }

        public static string ResolveSafeObjectName(string rootPrefix, string currentPrefix, string relativeName)
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
