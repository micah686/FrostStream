using System.Text.Json.Serialization;

namespace YtDlpSharpLib.Provisioning;

/// <summary>Top-level shape returned by the ffbinaries.com latest-version endpoint.</summary>
internal sealed record FfBinariesResponse
{
    /// <summary>The ffmpeg version string (e.g., <c>"4.4.1"</c>).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Permanent link to the release listing page.</summary>
    [JsonPropertyName("permalink")]
    public string? Permalink { get; init; }

    /// <summary>Per-platform download URL groups.</summary>
    [JsonPropertyName("bin")]
    public FfBinariesPlatforms? Bin { get; init; }
}

/// <summary>Platform-keyed groups of ffbinaries download URLs.</summary>
internal sealed record FfBinariesPlatforms
{
    /// <summary>Windows 64-bit binaries.</summary>
    [JsonPropertyName("windows-64")]
    public FfBinariesUrls? Windows64 { get; init; }

    /// <summary>Linux x86_64 binaries.</summary>
    [JsonPropertyName("linux-64")]
    public FfBinariesUrls? Linux64 { get; init; }

    /// <summary>Linux 32-bit binaries.</summary>
    [JsonPropertyName("linux-32")]
    public FfBinariesUrls? Linux32 { get; init; }

    /// <summary>Linux ARM64 binaries.</summary>
    [JsonPropertyName("linux-arm64")]
    public FfBinariesUrls? LinuxArm64 { get; init; }

    /// <summary>Linux ARM hard-float binaries.</summary>
    [JsonPropertyName("linux-armhf")]
    public FfBinariesUrls? LinuxArmHf { get; init; }

    /// <summary>macOS 64-bit binaries.</summary>
    [JsonPropertyName("osx-64")]
    public FfBinariesUrls? Osx64 { get; init; }
}

/// <summary>Pair of download URLs returned for a given platform.</summary>
internal sealed record FfBinariesUrls
{
    /// <summary>URL for the ffmpeg archive.</summary>
    [JsonPropertyName("ffmpeg")]
    public string? Ffmpeg { get; init; }

    /// <summary>URL for the ffprobe archive.</summary>
    [JsonPropertyName("ffprobe")]
    public string? Ffprobe { get; init; }
}
