namespace Shared;

/// <summary>
/// Represents a storage configuration retrieved from the database.
/// Uses FluentStorage connection strings to provide a unified interface across providers.
/// </summary>
public record StorageConfiguration
{
    /// <summary>
    /// Unique key identifying this configuration (e.g., "default", "premium-tier", "high-bandwidth").
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The storage method to use for this configuration.
    /// </summary>
    public required StorageMethod Method { get; init; }

    /// <summary>
    /// Optional description for this configuration.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Connection string for FluentStorage (e.g., "disk://path=/mnt/nfs",
    /// "ftp://host=...;user=...;password=...", "aws.s3://keyId=...;key=...;bucket=...").
    /// </summary>
    public required string ConnectionString { get; init; }

    // --- PosixLocal fields ---

    /// <summary>
    /// For PosixLocal: the directory path (local, NFS mount point, or SMB mount point).
    /// </summary>
    public string? DirectoryPath { get; init; }

    // --- StreamingNetwork fields ---

    /// <summary>
    /// For StreamingNetwork: the host to connect to.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// For StreamingNetwork: the port to connect on.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// For StreamingNetwork: the username for authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// For StreamingNetwork: the password for authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// For StreamingNetwork: the remote path to write to.
    /// </summary>
    public string? RemotePath { get; init; }

    // --- ObjectStorage fields ---

    /// <summary>
    /// For ObjectStorage: the bucket/container name.
    /// </summary>
    public string? BucketName { get; init; }

    /// <summary>
    /// For ObjectStorage: the cloud region.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// For ObjectStorage: the access key ID.
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>
    /// For ObjectStorage: the secret access key.
    /// </summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>
    /// For ObjectStorage: the service URL (for S3-compatible providers like MinIO).
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>
    /// Example configurations for development/testing.
    /// In production, these would come from PostgreSQL via DataBridge.
    /// </summary>
    public static class Examples
    {
        public static StorageConfiguration LocalDisk => new()
        {
            Key = "default",
            Method = StorageMethod.PosixLocal,
            ConnectionString = "disk://path=/tmp/staging",
            DirectoryPath = "/tmp/staging",
            Description = "Default local staging for co-located services"
        };

        public static StorageConfiguration NfsMount => new()
        {
            Key = "nfs-share",
            Method = StorageMethod.PosixLocal,
            ConnectionString = "disk://path=/mnt/nfs/staging",
            DirectoryPath = "/mnt/nfs/staging",
            Description = "NFS-mounted shared storage"
        };

        public static StorageConfiguration Sftp => new()
        {
            Key = "sftp-remote",
            Method = StorageMethod.StreamingNetwork,
            ConnectionString = "sftp://host=sftp.example.com;user=upload;password=secret",
            Host = "sftp.example.com",
            Username = "upload",
            Password = "secret",
            RemotePath = "/uploads",
            Description = "SFTP remote server for file transfers"
        };

        public static StorageConfiguration S3 => new()
        {
            Key = "aws-s3",
            Method = StorageMethod.ObjectStorage,
            ConnectionString = "aws.s3://keyId=AKIA...;key=secret;bucket=job-transfers;region=us-east-1",
            BucketName = "job-transfers",
            Region = "us-east-1",
            AccessKeyId = "AKIA...",
            SecretAccessKey = "secret",
            Description = "AWS S3 object storage for scale"
        };
    }
}
