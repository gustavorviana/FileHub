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
        /// Returns <c>false</c> for single-segment names — including those with
        /// a trailing <c>/</c> or <c>\</c> (e.g. <c>"foo/"</c>) — and exposes
        /// the normalized leaf via <paramref name="head"/> so callers can pass
        /// it straight to <c>ValidateName</c> without the separator. Also
        /// returns <c>false</c> for <c>null</c> or empty input, with
        /// <paramref name="head"/> set to <c>null</c>.
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

            // Strip trailing separators so "foo/" and "foo\\" collapse to a
            // single-segment name and "a/b/" still nests on "a" + "b".
            var trimmed = path.TrimEnd('/', '\\');
            if (trimmed.Length == 0)
                throw new FileHubException($"Absolute paths are not allowed: \"{path}\".");

            var sep = trimmed.IndexOfAny(new[] { '/', '\\' });
            if (sep < 0)
            {
                if (trimmed == "." || trimmed == "..")
                    throw new FileHubException($"Path \"{path}\" contains invalid segment \"{trimmed}\".");
                head = trimmed;
                return false;
            }

            var h = trimmed.Substring(0, sep);
            var r = trimmed.Substring(sep + 1).Trim('/', '\\');

            if (h == "." || h == "..")
                throw new FileHubException($"Path \"{path}\" contains invalid segment \"{h}\".");

            if (r.Length == 0)
            {
                head = h;
                return false;
            }

            head = h;
            rest = r;
            return true;
        }
    }
}
