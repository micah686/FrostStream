namespace Worker.Services;

public sealed class AssetCacheOptions
{
    public const string SectionName = "Cache:Assets";

    /// <summary>
    /// The storage backend (FluentStorage <c>storage_key</c>) that durable avatar/banner blobs
    /// are written to. The scheduler-driven channel refresh has no caller-supplied backend, so
    /// it falls back to this. Defaults to the conventional <c>"default"</c> key.
    /// </summary>
    public string StorageKey { get; set; } = "default";

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromDays(7);
}

public enum AssetKind
{
    Avatar = 0,
    Banner = 1
}
