using System;

namespace FileHub.Ftp
{
    /// <summary>
    /// Helpers specific to the FTP path model (absolute forward-slash paths).
    /// Name validation, glob matching and leaf extraction live in
    /// <see cref="PathUtil"/>, shared with every other driver.
    /// </summary>
    internal static class FtpPathUtil
    {
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

        public static string GetParent(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return "/";
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            if (idx <= 0) return "/";
            return trimmed.Substring(0, idx);
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

            // Collapse runs of `/` so a path like "/root//../etc" cannot pass
            // the StartsWith check by virtue of the duplicated separator
            // being treated as an opaque byte. PathUtil.ValidateName already
            // rejects ".." inside individual segments at the entry points, but
            // EnsureWithinRoot is the last line of defence and shouldn't
            // depend on what callers did upstream.
            var collapsed = CollapseSlashes(candidate);
            var normalizedCandidate = collapsed.TrimEnd('/');
            if (string.Equals(normalizedCandidate, rootPath, StringComparison.Ordinal)) return;

            var rootWithSep = rootPath.EndsWith("/", StringComparison.Ordinal) ? rootPath : rootPath + "/";
            if (!normalizedCandidate.StartsWith(rootWithSep, StringComparison.Ordinal))
                throw new FileHubException($"Access denied: \"{candidate}\" is outside the root \"{rootPath}\".");
        }

        private static string CollapseSlashes(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf("//", StringComparison.Ordinal) < 0)
                return path;
            var sb = new System.Text.StringBuilder(path.Length);
            char prev = '\0';
            foreach (var c in path)
            {
                if (c == '/' && prev == '/') continue;
                sb.Append(c);
                prev = c;
            }
            return sb.ToString();
        }

        public static string ResolveSafeChildPath(string rootPath, string currentPath, string relativeName)
        {
            PathUtil.ValidateName(relativeName);
            var candidate = Combine(currentPath, relativeName);
            EnsureWithinRoot(rootPath, candidate);
            return candidate;
        }
    }
}
