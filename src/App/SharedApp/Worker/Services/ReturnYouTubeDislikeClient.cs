using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Worker.Services;

public sealed class ReturnYouTubeDislikeClient(
    HttpClient httpClient,
    IOptions<WorkerOptions> options,
    ILogger<ReturnYouTubeDislikeClient> logger) : IReturnYouTubeDislikeClient
{
    private readonly ReturnYouTubeDislikeOptions _options = options.Value.ReturnYouTubeDislike;

    public async Task<ReturnYouTubeDislikeVotes?> GetVotesAsync(
        string videoId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        if (!_options.Enabled)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Timeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(_options.Timeout);
        }

        try
        {
            using var response = await httpClient.GetAsync(
                $"votes?videoId={Uri.EscapeDataString(videoId)}",
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Return YouTube Dislike had no votes for VideoId {VideoId}.", videoId);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Return YouTube Dislike rate-limited VideoId {VideoId}.", videoId);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Return YouTube Dislike returned HTTP {StatusCode} for VideoId {VideoId}.",
                    (int)response.StatusCode,
                    videoId);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonSerializer.DeserializeAsync(
                stream,
                ReturnYouTubeDislikeJsonContext.Default.ReturnYouTubeDislikeVotes,
                timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Return YouTube Dislike timed out for VideoId {VideoId}.", videoId);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Return YouTube Dislike request failed for VideoId {VideoId}.", videoId);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Return YouTube Dislike returned invalid JSON for VideoId {VideoId}.", videoId);
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReturnYouTubeDislikeVotes))]
internal sealed partial class ReturnYouTubeDislikeJsonContext : JsonSerializerContext;
