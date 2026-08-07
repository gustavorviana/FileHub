using System.Collections.Generic;

namespace FileHub
{
    /// <summary>
    /// Options applied at write time (<see cref="FileEntry.SetBytesAsync(byte[], FileWriteOptions, System.Threading.CancellationToken)"/>,
    /// <see cref="FileEntry.GetWriteStreamAsync(FileWriteOptions, System.Threading.CancellationToken)"/>,
    /// <see cref="FileEntry.CopyFromStreamAsync"/>).
    /// <para>
    /// Drivers that do not support a field silently ignore it — never throw.
    /// Honored fields per driver:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Local, FTP</b>: none — plain byte stores with no
    /// per-object metadata surface, so every field is ignored.</description></item>
    /// <item><description><b>Memory</b>: <see cref="ContentType"/>,
    /// <see cref="CacheControl"/>, <see cref="Metadata"/>;
    /// <see cref="StreamPreference"/> is ignored (the payload lives in
    /// process memory).</description></item>
    /// <item><description><b>AmazonS3, OracleObjectStorage</b>: all fields,
    /// including <see cref="StreamPreference"/> (multipart upload).</description></item>
    /// </list>
    /// <para>
    /// Drivers backed by storage with per-object metadata can expose typed
    /// subclasses surfacing backend-specific fields. Pass an instance of the
    /// subclass through the same parameter — drivers downcast where applicable.
    /// </para>
    /// </summary>
    public class FileWriteOptions
    {
        /// <summary>
        /// Hints how a write stream should commit. Drivers without multipart
        /// support ignore this value silently.
        /// </summary>
        public WriteStreamPreference StreamPreference { get; set; } = WriteStreamPreference.Auto;

        /// <summary>MIME content type (e.g. <c>"image/png"</c>). <c>null</c> = driver / backend default.</summary>
        public string ContentType { get; set; }

        /// <summary>HTTP <c>Cache-Control</c> header (e.g. <c>"public,max-age=3600"</c>). <c>null</c> = omit.</summary>
        public string CacheControl { get; set; }

        /// <summary>Free-form user metadata (key/value). Drivers map this to the backend's per-object key/value surface when available. Case-insensitive keys recommended.</summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; }
    }
}
