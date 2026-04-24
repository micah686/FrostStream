namespace Shared.Storage;

/// <summary>
/// Defines the available storage methods for file transfer.
/// </summary>
public enum StorageMethod
{
    /// <summary>
    /// POSIX-compatible local storage: local filesystem, NFS mounts, SMB/CIFS mounts.
    /// All accessed as a directory on the filesystem.
    /// </summary>
    PosixLocal,

    /// <summary>
    /// Streaming network storage: FTP, FTPS, SFTP.
    /// </summary>
    StreamingNetwork,

    /// <summary>
    /// Object storage: S3, Azure Blob, GCS, MinIO, and other blob stores.
    /// </summary>
    ObjectStorage
}
