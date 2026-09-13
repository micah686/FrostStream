using Shared.Messaging;
using Shared.Database;
using YtDlpSharpLib.Options;

namespace DataBridge.Lite;

public static class LiteAcquisitionKinds
{
    public const string Download = "download";
    public const string CreatorScan = "creator-scan";
    public const string PlaylistExpansion = "playlist-expansion";
    public const string LocalImport = "local-import";
}

public sealed record LiteDownloadSubmission
{
    public required string SourceUrl { get; init; }
    public string? StorageKey { get; init; }
    public string? PresetKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool ForceDownload { get; init; }
    public DownloadSourceKind SourceKind { get; init; } = DownloadSourceKind.Direct;
    public MediaKind MediaKind { get; init; } = MediaKind.Video;
    public AudioConversionFormat? AudioFormat { get; init; }
    public bool EncodeAudioRendition { get; init; }
    public bool FetchComments { get; init; }
    public int Priority { get; init; }
    public YtDlpOptions? YtDlpOptions { get; init; }
}

public sealed record LiteDownloadWork
{
    public required Guid JobId { get; init; }
    public required Guid RunId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string SourceUrl { get; init; }
    public required string StorageKey { get; init; }
    public string? PresetKey { get; init; }
    public string? CookieSecretPath { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool ForceDownload { get; init; }
    public DownloadSourceKind SourceKind { get; init; } = DownloadSourceKind.Direct;
    public MediaKind MediaKind { get; init; } = MediaKind.Video;
    public AudioConversionFormat? AudioFormat { get; init; }
    public bool EncodeAudioRendition { get; init; }
    public bool FetchComments { get; init; }
    public int Priority { get; init; }
    public YtDlpOptions? YtDlpOptions { get; init; }
}

public sealed record LitePlaylistSubmission
{
    public required string SourceUrl { get; init; }
    public string? StorageKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public bool EncodeForPlaylist { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
    public YtDlpOptions? YtDlpOptions { get; init; }
}

public sealed record LitePlaylistWork
{
    public required Guid PlaylistId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string SourceUrl { get; init; }
    public required string RequestedBy { get; init; }
    public required string StorageKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public bool EncodeForPlaylist { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
    public YtDlpOptions? YtDlpOptions { get; init; }
}

public record LiteCreatorScanSubmission
{
    public required long SourceId { get; init; }
    public CreatorSourceScanMode ScanMode { get; init; } = CreatorSourceScanMode.Incremental;
    public string? StorageKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public bool QueueDiscovered { get; init; } = true;
}

public sealed record LiteCreatorScanWork : LiteCreatorScanSubmission
{
    public required string RequestedBy { get; init; }
}

public sealed record LiteImportScanSubmission
{
    public string? SubPath { get; init; }
    public string StorageKey { get; init; } = "default";
}

public sealed record LiteImportScanWork
{
    public required Guid SessionId { get; init; }
    public string? SubPath { get; init; }
    public required string StorageKey { get; init; }
}

public sealed record LiteImportItemWork
{
    public required Guid SessionId { get; init; }
    public required Guid ItemId { get; init; }
}

public sealed class LiteAcquisitionOptions
{
    public const string SectionName = "LiteAcquisition";
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string TempPath { get; set; } = Path.Combine(Path.GetTempPath(), "froststream-lite", "acquisition");
    public string IncomingPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "incoming");
    public TimeSpan StorageProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MinimumDelayBetweenYtDlpStarts { get; set; } = TimeSpan.FromSeconds(3);
}

public sealed class LitePotOptions
{
    public const string SectionName = "LitePot";
    public bool Enabled { get; set; }
    public string? ProviderUrl { get; set; }
    public string ProxyBaseUrl { get; set; } = "http://127.0.0.1:5098/api/internal/pot/";
    public string? PluginDirectory { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
