using System.ComponentModel.DataAnnotations;

namespace Shared.Entities;

public class StorageConfigEntity
{
    public int Id { get; set; }

    [MaxLength(100)]
    public required string Key { get; set; }

    public StorageMethod Method { get; set; }

    /// <summary>
    /// JSON blob containing provider-specific connection parameters.
    /// Schema varies by <see cref="Method"/>:
    /// PosixLocal: { path }
    /// StreamingNetwork: { protocol, host, port, username, password, privateKey, publicKey, basePath }
    /// ObjectStorage: { provider, accessKeyId, secretKey, bucket, region, basePath }
    /// </summary>
    public required string Parameters { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
