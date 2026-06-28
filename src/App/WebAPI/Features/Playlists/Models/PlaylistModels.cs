using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace WebAPI.Features.Playlists.Models;

public sealed class PlaylistRequest
{
    [Required]
    [Url]
    public required string SourceUrl { get; init; }

    [DefaultValue("default")]
    public string? StorageKey { get; init; }
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
