namespace Worker.Services;

public sealed class AssetCacheOptions
{
    public const string SectionName = "Cache:Assets";

    public string Root { get; set; } = ".cache";

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
