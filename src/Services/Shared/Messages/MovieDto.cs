namespace Shared.Messages;

public record MovieDto
{
    public Guid Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public int ReleaseYear { get; init; }
    public int DurationMinutes { get; init; }
    public required string FilePath { get; init; }
    public string? XxHash { get; init; }
    public long FileSizeBytes { get; init; }
    public bool Verified { get; init; }
    public DateTime CreatedAt { get; init; }
}
