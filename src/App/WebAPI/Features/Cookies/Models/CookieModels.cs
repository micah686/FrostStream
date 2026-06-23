using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace WebAPI.Features.Cookies.Models;

public sealed class CookieUpsertRequest
{
    /// <summary>Netscape-formatted cookie text. Write-only: never returned over HTTP.</summary>
    [Required]
    [MinLength(1)]
    public required string Content { get; init; }

    /// <summary>Optional site/domain label shown in the profile list.</summary>
    [StringLength(255)]
    public string? Site { get; init; }

    /// <summary>Optional human-friendly name for the profile.</summary>
    [StringLength(255)]
    public string? DisplayName { get; init; }
}

/// <summary>Cookie profile metadata. Deliberately omits the cookie body.</summary>
public sealed record CookieProfileResponse
{
    public required string ProfileKey { get; init; }
    public string? Site { get; init; }
    public string? DisplayName { get; init; }
    public Instant? CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}
