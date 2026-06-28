using System.ComponentModel.DataAnnotations;

namespace WebAPI.Features.Notes.Models;

public sealed class UserNoteUpsertRequest
{
    [Required]
    [StringLength(8192, MinimumLength = 1)]
    public required string Note { get; init; }
}
