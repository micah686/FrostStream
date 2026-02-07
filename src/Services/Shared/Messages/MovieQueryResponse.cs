namespace Shared.Messages;

public record MovieQueryResponse
{
    public required List<MovieDto> Movies { get; init; }
    public int TotalCount { get; init; }
}
