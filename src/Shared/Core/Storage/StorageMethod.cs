namespace Shared.Storage;

/// <summary>
/// Defines the available storage methods for file transfer.
/// </summary>
public enum StorageMethod
{
    /// <summary>
    /// Local storage: local filesystem.
    /// All accessed as a directory on the filesystem.
    /// </summary>
    Local,

    /// <summary>
    /// Streaming network storage: FTP, FTPS, SFTP, NFS mounts, SMB/CIFS mounts.
    /// </summary>
    Network,

    /// <summary>
    /// Object storage: S3, Azure Blob, GCS, MinIO, and other blob stores.
    /// </summary>
    ObjectStorage
}
