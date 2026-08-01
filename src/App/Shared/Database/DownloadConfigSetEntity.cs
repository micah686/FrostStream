using NodaTime;

namespace Shared.Database;

public sealed class DownloadConfigSetEntity
{
    public long Id { get; set; }

    public required string OwnerSubject { get; set; }

    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public string? StorageKey { get; set; }

    public string? CookieProfileKey { get; set; }

    /// <summary>
    /// Optional worker tag constraining which worker instances may run jobs built from this
    /// config set. When set, it overrides the storage backend's own worker tag (<see cref="StorageConfigEntity.WorkerTag"/>)
    /// for every job dispatched using this config set.
    /// </summary>
    public string? WorkerTag { get; set; }

    public string? YtDlpOptionsJson { get; set; }

    /// <summary>
    /// Serialized <see cref="Shared.Downloads.IgnoreKeyword"/> list applied to user-initiated
    /// channel/playlist downloads using this set. Null/blank means no ignore filtering.
    /// </summary>
    public string? IgnoreKeywordsJson { get; set; }

    public int Priority { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}
