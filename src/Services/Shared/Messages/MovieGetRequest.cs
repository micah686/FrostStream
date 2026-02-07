namespace Shared.Messages;

public record MovieGetRequest
{
    public Guid MovieId { get; init; }
}
