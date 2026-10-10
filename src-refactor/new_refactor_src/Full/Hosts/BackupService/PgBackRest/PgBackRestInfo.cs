using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupService.PgBackRest;

/// <summary>
/// System.Text.Json models for `pgbackrest info --output=json` (a top-level array with one
/// element per stanza). Only the fields the service consumes are mapped; unknown fields are
/// ignored so pgBackRest upgrades don't break parsing.
/// </summary>
internal sealed record PgBackRestStanzaInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public PgBackRestStatus? Status { get; init; }

    [JsonPropertyName("backup")]
    public IReadOnlyList<PgBackRestBackup> Backup { get; init; } = [];

    [JsonPropertyName("archive")]
    public IReadOnlyList<PgBackRestArchive> Archive { get; init; } = [];
}

internal sealed record PgBackRestStatus
{
    /// <summary>0 = ok, 1 = missing stanza path, 2 = no valid backups, 3 = missing stanza data, …</summary>
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed record PgBackRestArchive
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Oldest archived WAL segment.</summary>
    [JsonPropertyName("min")]
    public string? Min { get; init; }

    /// <summary>Newest archived WAL segment.</summary>
    [JsonPropertyName("max")]
    public string? Max { get; init; }
}

internal sealed record PgBackRestBackup
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>"full", "diff", or "incr".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("error")]
    public bool? Error { get; init; }

    [JsonPropertyName("annotation")]
    public IReadOnlyDictionary<string, string>? Annotation { get; init; }

    [JsonPropertyName("timestamp")]
    public PgBackRestTimestamp? Timestamp { get; init; }

    [JsonPropertyName("info")]
    public PgBackRestBackupSize? Info { get; init; }

    [JsonPropertyName("archive")]
    public PgBackRestWalRange? WalRange { get; init; }

    /// <summary>Label of the prior backup a diff/incr depends on; null for full backups.</summary>
    [JsonPropertyName("prior")]
    public string? Prior { get; init; }

    [JsonIgnore]
    public DateTimeOffset? StartedAt
        => Timestamp?.Start is { } start ? DateTimeOffset.FromUnixTimeSeconds(start) : null;

    [JsonIgnore]
    public DateTimeOffset? CompletedAt
        => Timestamp?.Stop is { } stop ? DateTimeOffset.FromUnixTimeSeconds(stop) : null;

    [JsonIgnore]
    public string? AnnotatedName
        => Annotation is not null && Annotation.TryGetValue("name", out var name) ? name : null;
}

internal sealed record PgBackRestTimestamp
{
    [JsonPropertyName("start")]
    public long? Start { get; init; }

    [JsonPropertyName("stop")]
    public long? Stop { get; init; }
}

internal sealed record PgBackRestBackupSize
{
    /// <summary>Uncompressed database size.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("repository")]
    public PgBackRestRepositorySize? Repository { get; init; }
}

internal sealed record PgBackRestRepositorySize
{
    /// <summary>Compressed size this backup adds to the repository.</summary>
    [JsonPropertyName("delta")]
    public long? Delta { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }
}

internal sealed record PgBackRestWalRange
{
    [JsonPropertyName("start")]
    public string? Start { get; init; }

    [JsonPropertyName("stop")]
    public string? Stop { get; init; }
}

internal static class PgBackRestInfoParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns the requested stanza, or null when the repository has never been initialized
    /// (pgbackrest still emits a `[]` or a placeholder stanza with a non-zero status).
    /// </summary>
    public static PgBackRestStanzaInfo? Parse(string json, string stanza)
    {
        var stanzas = JsonSerializer.Deserialize<List<PgBackRestStanzaInfo>>(json, JsonOptions) ?? [];
        return stanzas.FirstOrDefault(s => string.Equals(s.Name, stanza, StringComparison.Ordinal));
    }
}
