using System.ComponentModel.DataAnnotations;

namespace WebAPI.Features.Cookies.Models;

public sealed class CookieUpsertRequest
{
    [Required]
    [MinLength(1)]
    public required string Content { get; init; }
}

public sealed record CookieResponse(string Key);
