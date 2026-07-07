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
        /// Returns <c>true</c> if <paramref name="name"/> is nothing but a
        /// single leaf (contains no <c>/</c> or <c>\</c> separator). A quick,
        /// allocation-free pre-check so callers can skip the nested-path
        /// machinery on the common single-segment case.
        /// </summary>
        public static bool HasSeparator(string name)
            => !string.IsNullOrEmpty(name) && (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0);

        /// <summary>
        /// Splits a caller-supplied name at its <em>last</em> separator so the
        /// tail is the real entry name and everything before it is the path:
        /// <c>"a/b/c.txt"</c> → <paramref name="subPath"/> <c>"a/b"</c>,
        /// <paramref name="leaf"/> <c>"c.txt"</c>. Every segment is validated
        /// (rejects <c>.</c>/<c>..</c> and, via <see cref="PathUtil.ValidateName"/>,
        /// separators-in-segment and control chars). Returns <c>false</c> for a
        /// single-segment name (or one with only a trailing separator, e.g.
        /// <c>"foo/"</c>), exposing the normalized leaf via
        /// <paramref name="leaf"/> and leaving <paramref name="subPath"/>
        /// <c>null</c>.
        /// </summary>
        public static bool TrySplitLeaf(string name, out string subPath, out string leaf)
        {
            subPath = null;
            leaf = null;
            var segments = PathUtil.SplitAndValidateSegments(name);
            if (segments.Length == 0) return false;

            leaf = segments[segments.Length - 1];
            if (segments.Length == 1) return false;

            subPath = string.Join("/", segments, 0, segments.Length - 1);
            return true;
        }

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
