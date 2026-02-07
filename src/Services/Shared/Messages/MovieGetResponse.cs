namespace Shared.Messages;

public record MovieGetResponse
{
    public MovieDto? Movie { get; init; }
    public string? StorageConnectionString { get; init; }
    public string? StoragePath { get; init; }
}
