namespace FileHub.OracleObjectStorage
{
    /// <summary>
    /// OCI-specific write options. Extends <see cref="FileWriteOptions"/>.
    /// Currently surfaces no extra fields beyond the base type — reserved
    /// for OCI-only knobs (e.g. <c>storage-tier</c>) as they are wired
    /// through the internal client.
    /// </summary>
    public class OciWriteOptions : FileWriteOptions
    {
    }
}
