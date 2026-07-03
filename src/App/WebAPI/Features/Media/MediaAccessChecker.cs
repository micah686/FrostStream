using Conduit.NATS;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using System.Security.Claims;

namespace WebAPI.Features.Media;

/// <summary>
/// Evaluates watch-time access to a media item via DataBridge. Shared by the progressive watch
/// endpoints and the HLS streaming endpoints. Returns <c>null</c> when playback is permitted, or a
/// non-null result (403 when restricted, 503 when the check is unreachable) that the caller must
/// return instead of serving the stream. Fails closed.
/// </summary>
public sealed class MediaAccessChecker(IMessageBus messageBus, ILogger<MediaAccessChecker> logger)
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public async Task<IActionResult?> CheckWatchAccessAsync(
        ClaimsPrincipal? user,
        Guid mediaGuid,
        CancellationToken cancellationToken)
    {
        // Cast tokens are scoped to a single media item; a token minted for one GUID must not
        // unlock any other, regardless of what the issuing user could otherwise watch.
        var castScope = user?.FindFirst(CastTokenClaims.MediaGuid)?.Value;
        if (castScope is not null &&
            (!Guid.TryParse(castScope, out var scopedGuid) || scopedGuid != mediaGuid))
        {
            return new ObjectResult("The cast token does not grant access to this media item.")
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        var groups = (user?.FindAll(AuthConstants.GroupsClaim) ?? [])
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        MediaAccessCheckResponseMessage? response;
        try
        {
            response = await messageBus.RequestAsync<MediaAccessCheckRequestMessage, MediaAccessCheckResponseMessage>(
                MediaAccessSubjects.Check,
                new MediaAccessCheckRequestMessage { MediaGuid = mediaGuid, UserGroups = groups },
                QueryTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed checking media access for {MediaGuid}.", mediaGuid);
            return Unavailable();
        }

        if (response is null)
        {
            return Unavailable();
        }

        return response.IsAllowed
            ? null
            : new ObjectResult("You are not allowed to watch this media item.")
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
    }

    private static ObjectResult Unavailable()
        => new("DataBridge is unreachable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
}
