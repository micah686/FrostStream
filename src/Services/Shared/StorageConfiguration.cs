namespace Shared;

/// <summary>
/// Represents a storage configuration retrieved from the database.
/// This is a placeholder model that will be populated by DataBridge from PostgreSQL.
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
    /// For LocalStaging: the shared staging directory path.
    /// </summary>
    public string? StagingPath { get; init; }

    /// <summary>
    /// For ObjectStore: the bucket name to use.
    /// </summary>
    public string? ObjectStoreBucket { get; init; }

    /// <summary>
    /// For DirectExternal: the presigned URL or endpoint.
    /// </summary>
    public string? ExternalEndpoint { get; init; }

    /// <summary>
    /// Optional description for this configuration.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Example configurations for development/testing.
    /// In production, these would come from PostgreSQL via DataBridge.
    /// </summary>
    public static class Examples
    {
        public static StorageConfiguration Default => new()
        {
            Key = "default",
            Method = StorageMethod.LocalStaging,
            StagingPath = "/tmp/staging",
            Description = "Default local staging for co-located services"
        };

        public static StorageConfiguration HighBandwidth => new()
        {
            Key = "high-bandwidth",
            Method = StorageMethod.DirectStreaming,
            Description = "Direct streaming for high-bandwidth connections"
        };

        public static StorageConfiguration Resilient => new()
        {
            Key = "resilient",
            Method = StorageMethod.ObjectStore,
            ObjectStoreBucket = "job-transfers",
            Description = "NATS Object Store for ephemeral workers requiring reliability"
        };

        public static StorageConfiguration External => new()
        {
            Key = "external-s3",
            Method = StorageMethod.DirectExternal,
            ExternalEndpoint = "s3://my-bucket/uploads",
            Description = "Direct S3 upload for scale and external destinations"
        };
    }
}
