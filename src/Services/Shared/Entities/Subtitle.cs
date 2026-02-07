namespace Shared.Entities;

/// <summary>
/// Represents subtitle files for movies
/// </summary>
public class Subtitle
{
    public Guid Id { get; set; }
    public Guid MovieId { get; set; }
    public required string Language { get; set; }
    public required string FilePath { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation property
    public Movie? Movie { get; set; }
}
