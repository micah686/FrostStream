namespace Worker.Services;

/// <summary>
/// Configuration for this worker instance's tag-based job routing.
///
/// Workers with an empty <see cref="Tags"/> list and <see cref="AcceptsUntaggedJobs"/> true
/// (the default) behave identically to the pre-tag behaviour: they compete for all
/// download commands on the untagged consumers.
///
/// Workers with tags subscribe to per-tag consumers (e.g.
/// <c>worker-fetch-metadata-nas</c> for tag <c>"nas"</c>), so DataBridge can route
/// storage-affine jobs exclusively to workers that can reach that backend.
/// Setting <see cref="AcceptsUntaggedJobs"/> false on a tagged worker means it will
/// only process jobs explicitly tagged for one of its tags.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Tags this worker instance advertises. Each tag generates a dedicated NATS consumer.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Whether this worker also competes for jobs that have no required worker tag.
    /// Defaults to <see langword="true"/> so untagged deployments are unaffected.
    /// </summary>
    public bool AcceptsUntaggedJobs { get; init; } = true;

    /// <summary>
    /// Static filesystem folder this worker discovers local-import content in. The worker creates
    /// it at startup (with a <c>manifest.json.template</c>) and reads <c>manifest.json</c> plus the
    /// listed media files from it. Manifest paths are resolved relative to this root.
    /// </summary>
    public string IncomingRoot { get; init; } =
        Path.Combine(AppContext.BaseDirectory, Shared.Imports.LocalImportIncoming.FolderName);

    /// <summary>Worker-wide yt-dlp <c>--limit-rate</c> value, e.g. <c>500K</c> or <c>4.2M</c>.</summary>
    public string? YtDlpLimitRate { get; init; }

    /// <summary>Worker-wide yt-dlp <c>--throttled-rate</c> value, e.g. <c>100K</c>.</summary>
    public string? YtDlpThrottledRate { get; init; }

    /// <summary>
    /// Minimum time to wait between yt-dlp process starts on this worker. Value is a
    /// TimeSpan string (e.g. <c>00:00:05</c>, env var <c>Worker__YtDlpMinDelayBetweenStarts</c>),
    /// not a bare number of seconds. Defaults to 3 seconds when unset; configured values
    /// below 3 seconds are clamped up to 3 seconds.
    /// </summary>
    public TimeSpan? YtDlpMinDelayBetweenStarts { get; init; }

    /// <summary>Floor applied to <see cref="YtDlpMinDelayBetweenStarts"/>.</summary>
    public static readonly TimeSpan YtDlpMinDelayFloor = TimeSpan.FromSeconds(3);

    /// <summary>The delay actually applied between yt-dlp process starts: configured value clamped to at least the 3s floor.</summary>
    public TimeSpan EffectiveYtDlpMinDelay() =>
        YtDlpMinDelayBetweenStarts is { } configured && configured > YtDlpMinDelayFloor
            ? configured
            : YtDlpMinDelayFloor;

    /// <summary>Return YouTube Dislike enrichment settings.</summary>
    public ReturnYouTubeDislikeOptions ReturnYouTubeDislike { get; init; } = new();
}

public sealed class ReturnYouTubeDislikeOptions
{
    /// <summary>Whether YouTube metadata should be enriched from Return YouTube Dislike.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base URL for the Return YouTube Dislike API.</summary>
    public Uri BaseUrl { get; init; } = new("https://returnyoutubedislikeapi.com/");

    /// <summary>Timeout for a single Return YouTube Dislike request.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}
