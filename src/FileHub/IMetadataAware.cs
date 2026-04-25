namespace FileHub
{
    /// <summary>
    /// Capability: the file exposes a mutable <see cref="FileMetadata"/>
    /// surface (key/value tags plus driver-specific typed fields like S3's
    /// StorageClass). Only drivers with a native per-object metadata API
    /// implement this — S3 and OCI. Local, Memory, and FTP do not.
    ///
    /// <para>
    /// Usage: read current values via <c>file.Metadata</c>; mutate
    /// properties to stage changes; call any alteration operation
    /// (<c>SetBytes</c>, <c>GetWriteStream</c>, <c>CopyTo</c>,
    /// <c>MoveTo</c>). When the driver sees
    /// <see cref="FileMetadata.IsModified"/> is <c>true</c>, it applies
    /// the staged values (via CopyObject REPLACE on S3, equivalent on
    /// OCI) and clears the flag. When not dirty, server defaults /
    /// source-inherit behavior applies.
    /// </para>
    ///
    /// <para>
    /// Canonical "update metadata of an existing object without
    /// re-uploading":
    /// <code>
    /// file.Metadata.StorageClass = "GLACIER";
    /// file.CopyTo(file.Parent, file.Name);   // self-copy applies the change
    /// </code>
    /// </para>
    /// </summary>
    public interface IMetadataAware
    {
        FileMetadata Metadata { get; }
    }
}
