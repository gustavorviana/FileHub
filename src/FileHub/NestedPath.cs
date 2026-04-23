using System;

namespace FileHub
{
    /// <summary>
    /// Shared helpers for splitting caller-supplied directory names that use
    /// <c>/</c> or <c>\</c> as segment separators. Used by
    /// <see cref="FileDirectory"/> implementations so that
    /// <c>CreateDirectory("a/b/c")</c> and <c>TryOpenDirectory("a/b/c", out _)</c>
    /// descend through intermediate directories uniformly across drivers.
    /// </summary>
    public static class NestedPath
    {
        /// <summary>
        /// Splits <paramref name="path"/> at the first <c>/</c> or <c>\</c>.
        /// Returns <c>true</c> when the input is a genuinely nested path and
        /// exposes the first segment via <paramref name="head"/> and the rest
        /// (leading/trailing separators trimmed) via <paramref name="rest"/>.
        /// Returns <c>false</c> for single-segment names (no separator, empty,
        /// or trailing-separator-only) — callers should fall through to their
        /// normal single-segment code path in that case.
        /// </summary>
        /// <exception cref="FileHubException">
        /// Thrown when <paramref name="path"/> is absolute (starts with a
        /// separator), or contains <c>.</c> or <c>..</c> as a segment.
        /// </exception>
        public static bool TrySplit(string path, out string head, out string rest)
        {
            head = null;
            rest = null;
            if (string.IsNullOrEmpty(path)) return false;
            if (path[0] == '/' || path[0] == '\\')
                throw new FileHubException($"Absolute paths are not allowed: \"{path}\".");

            var sep = path.IndexOfAny(new[] { '/', '\\' });
            if (sep < 0) return false;

            var h = path.Substring(0, sep);
            var r = path.Substring(sep + 1).Trim('/', '\\');

            if (h == "." || h == "..")
                throw new FileHubException($"Path \"{path}\" contains invalid segment \"{h}\".");

            if (r.Length == 0) return false;

            head = h;
            rest = r;
            return true;
        }
    }
}
