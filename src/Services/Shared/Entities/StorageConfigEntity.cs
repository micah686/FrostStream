namespace Shared.Entities;

/// <summary>
/// Represents a storage configuration stored in the database.
/// Services request these configurations via NATS to obtain FluentStorage connection strings.
/// </summary>
public class StorageConfigEntity
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Unique key identifying this configuration (e.g., "default", "premium-tier", "high-bandwidth").
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// The storage method to use for this configuration.
    /// </summary>
    public required StorageMethod Method { get; set; }

    /// <summary>
    /// FluentStorage connection string (e.g., "disk://path=/mnt/nfs",
    /// "ftp://host=...;user=...;password=...", "aws.s3://keyId=...;key=...;bucket=...").
    /// </summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// Optional description for this configuration.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional sub-path within the storage for this configuration (e.g., remote directory, object prefix).
    /// </summary>
    public string? RemotePath { get; set; }

    /// <summary>
    /// When this configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this configuration was last updated.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
