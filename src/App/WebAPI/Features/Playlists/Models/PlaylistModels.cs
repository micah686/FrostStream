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

    public string? RequestedBy { get; init; }
}

public sealed record PlaylistRequestResponse(Guid PlaylistId, Guid CorrelationId);
