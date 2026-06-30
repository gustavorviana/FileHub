using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Options applied at write time (<see cref="FileEntry.SetBytesAsync(byte[], FileWriteOptions, System.Threading.CancellationToken)"/>,
    /// <see cref="FileEntry.GetWriteStreamAsync(FileWriteOptions, System.Threading.CancellationToken)"/>,
    /// <see cref="FileEntry.CopyFromStreamAsync"/>).
    /// <para>
    /// Drivers that do not support a field silently ignore it — never throw. Check
    /// the driver's documentation to know which fields it actually applies.
    /// </para>
    /// <para>
    /// Drivers backed by storage with per-object metadata can expose typed
    /// subclasses surfacing backend-specific fields. Pass an instance of the
    /// subclass through the same parameter — drivers downcast where applicable.
    /// </para>
    /// </summary>
    public class FileWriteOptions
    {
        /// <summary>MIME content type (e.g. <c>"image/png"</c>). <c>null</c> = driver / backend default.</summary>
        public string ContentType { get; set; }

        /// <summary>HTTP <c>Cache-Control</c> header (e.g. <c>"public,max-age=3600"</c>). <c>null</c> = omit.</summary>
        public string CacheControl { get; set; }

        /// <summary>Free-form user metadata (key/value). Drivers map this to the backend's per-object key/value surface when available. Case-insensitive keys recommended.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }
    }
}
