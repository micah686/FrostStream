using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Shared;

[Index(nameof(Key), IsUnique = true, Name = "uq_storage_keys_key")]
public class StorageConfigEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; init; }
    
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    public StorageMethod Method { get; set; }

    /// <summary>
    /// JSON blob containing provider-specific connection parameters.
    /// Schema varies by <see cref="Method"/>:
    /// PosixLocal: { path }
    /// StreamingNetwork: { protocol, host, port, username, password, privateKey, publicKey, basePath }
    /// ObjectStorage: { provider, accessKeyId, secretKey, bucket, region, basePath }
    /// </summary>
    [Required]
    public required string Parameters { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }
}
