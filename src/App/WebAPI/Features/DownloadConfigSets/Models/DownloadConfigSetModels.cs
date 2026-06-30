using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shared.Downloads;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.DownloadConfigSets.Models;

public class DownloadConfigSetCreateRequest
{
    [Required]
    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public required string Key { get; init; }

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2000)]
    public string? Description { get; init; }

    [DefaultValue("default")]
    public string? StorageKey { get; init; }

    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? CookieProfileKey { get; init; }

    public YtDlpOptions? YtDlpOptions { get; init; }

    /// <summary>Title keywords that suppress videos during user-initiated channel/playlist downloads
    /// using this config set. Background channel monitoring ignores this list.</summary>
    public IReadOnlyList<IgnoreKeyword> IgnoreKeywords { get; init; } = [];

    [DefaultValue(false)]
    public bool EncodeForPlaylist { get; init; }

    [DefaultValue(AudioRenditionFormat.Aac)]
    public AudioRenditionFormat AudioFormat { get; init; } = AudioRenditionFormat.Aac;

    [Range(0, 100)]
    public int Priority { get; init; }

    [DefaultValue(false)]
    public bool FetchComments { get; init; }
}

public sealed class DownloadConfigSetUpdateRequest : DownloadConfigSetCreateRequest;

public sealed class DownloadConfigSetResponse
{
    public required long Id { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? StorageKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public YtDlpOptions? YtDlpOptions { get; init; }
    public IReadOnlyList<IgnoreKeyword> IgnoreKeywords { get; init; } = [];
    public bool EncodeForPlaylist { get; init; }
    public AudioRenditionFormat AudioFormat { get; init; }
    public int Priority { get; init; }
    public bool FetchComments { get; init; }
}
