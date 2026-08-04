using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace FileHub
{
    /// <summary>
    /// Path and name rules shared by every driver, so a name accepted by one
    /// hub is accepted by all of them. Validation is portable by design — it
    /// does not depend on the OS the code happens to run on; drivers backed
    /// by a real file system layer <see cref="ValidateLocalName"/> on top to
    /// also reject the characters the local OS forbids.
    /// </summary>
    public static class PathUtil
    {
        private static readonly ConcurrentDictionary<string, Regex> _regexCache =
            new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>
        /// Validates a single path segment (file or directory name) against
        /// the portable rule set: not null/empty, not <c>.</c> or <c>..</c>,
        /// no path separators, no control characters.
        /// </summary>
        public static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));
            if (name == "." || name == "..")
                throw new ArgumentException($"Name \"{name}\" is not allowed.", nameof(name));
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                throw new ArgumentException($"Name \"{name}\" must not contain path separators.", nameof(name));
            if (name.Any(char.IsControl))
                throw new ArgumentException($"Name \"{name}\" contains control characters.", nameof(name));
        }

        /// <summary>
        /// <see cref="ValidateName"/> plus the characters the local OS forbids
        /// in file names. Only for drivers backed by a real file system — the
        /// extra characters vary per OS, so remote drivers must not use this.
        /// </summary>
        public static void ValidateLocalName(string name)
        {
            ValidateName(name);
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"Name \"{name}\" contains invalid characters.", nameof(name));
        }

        /// <summary>
        /// Splits a caller-supplied nested name (<c>/</c> or <c>\</c>
        /// separated) into segments, rejecting <c>.</c> / <c>..</c> traversal
        /// with <see cref="FileHubException"/> (aligned with
        /// <see cref="NestedPath.TrySplit"/>) and validating each segment with
        /// <see cref="ValidateName"/>.
        /// </summary>
        public static string[] SplitAndValidateSegments(string nestedName)
            => SplitAndValidateSegments(nestedName, ValidateName);

        /// <summary>
        /// Same as <see cref="SplitAndValidateSegments(string)"/> but with a
        /// caller-chosen per-segment validator (e.g.
        /// <see cref="ValidateLocalName"/> for OS-backed drivers).
        /// </summary>
        public static string[] SplitAndValidateSegments(string nestedName, Action<string> validateSegment)
        {
            if (string.IsNullOrEmpty(nestedName))
                throw new ArgumentException("Name cannot be null or empty.", nameof(nestedName));

            if (nestedName[0] == '/' || nestedName[0] == '\\')
                throw new FileHubException($"Absolute paths are not allowed; \"{nestedName}\" must be relative.");

            var normalized = nestedName.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0)
                throw new FileHubException($"Absolute paths are not allowed; \"{nestedName}\" must be relative.");

            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                if (seg == "." || seg == "..")
                    throw new FileHubException($"Path \"{nestedName}\" contains invalid segment \"{seg}\".");
                validateSegment(seg);
            }
            return segments;
        }

        /// <summary>
        /// Compiles a <c>*</c>/<c>?</c> glob into a cached case-insensitive
        /// regex. Every driver uses this same contract so a pattern like
        /// <c>"report_?.csv"</c> yields the same result everywhere.
        /// <para>
        /// Matching is <b>intentionally case-insensitive on every backend</b>,
        /// including Linux. This is a deliberate divergence from
        /// <see cref="System.IO.Directory.GetFiles(string, string)"/>, whose
        /// case sensitivity follows the underlying filesystem
        /// (case-sensitive on Linux, insensitive on Windows) — chosen so a glob
        /// behaves identically across drivers and operating systems.
        /// </para>
        /// </summary>
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

        /// <summary>Last segment of a path or prefix, trailing separator ignored.</summary>
        public static string GetLeafName(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return string.Empty;

            var trimmed = path.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            return idx < 0 ? trimmed : trimmed.Substring(idx + 1);
        }

        // === Object-storage prefix model (S3 / OCI) ===
        // Directories are a fiction over flat keys: a directory is a prefix
        // ending in "/" (empty string = bucket root), a file is a full key.

        /// <summary>
        /// Normalises a caller-supplied root path to the canonical prefix
        /// form: no leading slash, single trailing slash, empty for the root.
        /// </summary>
        public static string NormalizePrefix(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return string.Empty;

            var trimmed = path.Replace('\\', '/').TrimStart('/');
            if (trimmed.Length == 0)
                return string.Empty;

            return trimmed.EndsWith("/") ? trimmed : trimmed + "/";
        }

        /// <summary>Object key for a file directly under a prefix.</summary>
        public static string CombineKey(string parentPrefix, string fileName)
        {
            return (parentPrefix ?? string.Empty) + fileName;
        }

        /// <summary>Prefix for a child directory under a prefix.</summary>
        public static string CombinePrefix(string parentPrefix, string childName)
        {
            return (parentPrefix ?? string.Empty) + childName + "/";
        }

        /// <summary>Filesystem-like display path for a prefix ("/" for the root).</summary>
        public static string DisplayPath(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return "/";
            return "/" + prefix.TrimEnd('/');
        }

        /// <summary>
        /// Joins path <paramref name="parts"/> using the driver-neutral
        /// <c>/</c> separator, collapsing separators where a part already ends
        /// (or the next begins) with one — e.g. the object-storage / FTP root
        /// <c>"/"</c>. Empty/null parts are skipped. Single joining rule for
        /// every logical driver so display paths and exception messages never
        /// carry a doubled <c>"//"</c> at the root.
        /// </summary>
        public static string JoinDisplay(params string[] parts)
        {
            if (parts == null || parts.Length == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part))
                    continue;

                if (sb.Length == 0)
                {
                    sb.Append(part);
                    continue;
                }

                if (sb[sb.Length - 1] != '/')
                    sb.Append('/');
                    
                sb.Append(part[0] == '/' ? part.Substring(1) : part);
            }
            return sb.ToString();
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
            var boundedRoot = rootPrefix[rootPrefix.Length - 1] == '/' ? rootPrefix : rootPrefix + "/";
            if (candidate == null
                || (!candidate.StartsWith(boundedRoot, StringComparison.Ordinal)
                    && !string.Equals(candidate, rootPrefix, StringComparison.Ordinal)))
                throw new FileHubException($"Access denied: \"{candidate}\" is outside the root \"{rootPrefix}\".");
        }

        /// <summary>Validates the name, appends it to the prefix and enforces root confinement.</summary>
        public static string ResolveSafeKey(string rootPrefix, string currentPrefix, string relativeName)
        {
            ValidateName(relativeName);
            var candidate = (currentPrefix ?? string.Empty) + relativeName;
            EnsureWithinRootPrefix(rootPrefix, candidate);
            return candidate;
        }

        /// <summary>Directory counterpart of <see cref="ResolveSafeKey"/> — resolves a child prefix.</summary>
        public static string ResolveSafeChildPrefix(string rootPrefix, string currentPrefix, string relativeName)
        {
            ValidateName(relativeName);
            var candidate = (currentPrefix ?? string.Empty) + relativeName + "/";
            EnsureWithinRootPrefix(rootPrefix, candidate);
            return candidate;
        }
    }
}
