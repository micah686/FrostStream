namespace Shared.Entities;

/// <summary>
/// Represents a user in the system
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // Navigation property
    public ICollection<Movie> FavoriteMovies { get; set; } = new List<Movie>();
}
