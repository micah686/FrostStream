namespace Worker.Services;

public interface IReturnYouTubeDislikeClient
{
    Task<ReturnYouTubeDislikeVotes?> GetVotesAsync(string videoId, CancellationToken cancellationToken);
}

public sealed record ReturnYouTubeDislikeVotes
{
    public string? Id { get; init; }
    public long? Likes { get; init; }
    public long? Dislikes { get; init; }
    public double? Rating { get; init; }
    public long? ViewCount { get; init; }
}
