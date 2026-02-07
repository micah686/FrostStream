namespace Shared.Entities;

/// <summary>
/// Represents movie metadata
/// </summary>
public class Movie
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int ReleaseYear { get; set; }
    public int DurationMinutes { get; set; }
    public required string FilePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<Subtitle> Subtitles { get; set; } = new List<Subtitle>();
    public ICollection<User> FavoritedByUsers { get; set; } = new List<User>();
}
