using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shared.Messaging;
using YtDlpSharpLib.Options;

namespace WebAPI.Features.Playlists.Models;

public sealed class PlaylistRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public string? StorageKey { get; init; }

    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? ConfigSetKey { get; init; }

    [StringLength(100, MinimumLength = 2)]
    [RegularExpression("^[a-z0-9-]{2,100}$")]
    public string? CookieProfileKey { get; init; }

    public YtDlpOptions? YtDlpOptions { get; init; }

    [DefaultValue(false)]
    public bool? EncodeForPlaylist { get; init; }

    [Range(0, 100)]
    public int? Priority { get; init; }

    public bool? FetchComments { get; init; }
}

public sealed record PlaylistRequestResponse(Guid PlaylistId, Guid CorrelationId);

public sealed class UserPlaylistCreateRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2048)]
    public string? Description { get; init; }
}

public sealed class UserPlaylistUpdateRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(2048)]
    public string? Description { get; init; }
}

public sealed class UserPlaylistAddItemRequest
{
    public required Guid MediaGuid { get; init; }

    [Range(1, int.MaxValue)]
    public int? Position { get; init; }
}

public sealed class UserPlaylistReorderRequest
{
    [Required]
    public required IReadOnlyList<Guid> MediaGuids { get; init; }
}
