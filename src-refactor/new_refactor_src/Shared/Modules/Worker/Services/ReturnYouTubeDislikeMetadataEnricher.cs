using YtDlpSharpLib.Models;

namespace Worker.Services;

internal static class ReturnYouTubeDislikeMetadataEnricher
{
    public static async Task<VideoInfo> EnrichAsync(
        VideoInfo info,
        IReturnYouTubeDislikeClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(client);

        var provider = !string.IsNullOrWhiteSpace(info.Extractor)
            ? info.Extractor
            : info.ExtractorKey;
        var sourceMediaId = info.Id ?? info.DisplayId;

        if (!IsYouTubeProvider(provider) || string.IsNullOrWhiteSpace(sourceMediaId))
        {
            return info;
        }

        var votes = await client.GetVotesAsync(sourceMediaId, cancellationToken);
        if (votes is null)
        {
            return info;
        }

        return info with
        {
            LikeCount = info.LikeCount ?? votes.Likes,
            DislikeCount = info.DislikeCount ?? votes.Dislikes,
            ViewCount = info.ViewCount ?? votes.ViewCount,
            AverageRating = info.AverageRating ?? votes.Rating
        };
    }

    private static bool IsYouTubeProvider(string? provider)
        => provider is not null
           && provider.Contains("youtube", StringComparison.OrdinalIgnoreCase);
}
