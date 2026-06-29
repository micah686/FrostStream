using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using WebAPI.Auth;

namespace WebAPI.Features.Media.Controllers;

/// <summary>
/// Administers watch-time media access control: per-media group allow-lists, per-provider group
/// allow-lists, and tiered age-limit policies. Every action is gated by the
/// <c>media-access-admin</c> bundle, so only granted administrators/moderators can change who may
/// watch which media. Enforcement of these rules happens in the stream endpoints.
/// </summary>
[ApiController]
[Route("api/media-access")]
public sealed class MediaAccessController(
    IMessageBus messageBus,
    ILogger<MediaAccessController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // --- Per-media restrictions -------------------------------------------

    [HttpGet("media/{mediaGuid:guid}/groups")]
    [Endpoint(EndpointIds.MediaAccessMediaList)]
    [EndpointSummary("List the groups allowed to watch a media item")]
    [EndpointDescription("Returns the group allow-list restricting playback of a single media item. An empty list means the item is unrestricted and any user with media access can watch it.")]
    public async Task<IActionResult> ListMediaGroups(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.MediaList,
            new MediaAccessMediaListRequestMessage { MediaGuid = mediaGuid },
            cancellationToken);

        return response is null
            ? Unavailable()
            : MapResult(response, () => Ok(response.Groups ?? []));
    }

    [HttpPost("media/{mediaGuid:guid}/groups/{groupName}")]
    [Endpoint(EndpointIds.MediaAccessMediaAdd)]
    [EndpointSummary("Allow a group to watch a media item")]
    [EndpointDescription("Adds a group to a media item's playback allow-list. Once any group is added, only members of an allowed group may watch the item.")]
    public async Task<IActionResult> AddMediaGroup(Guid mediaGuid, string groupName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.MediaAdd,
            new MediaAccessMediaMutateRequestMessage
            {
                MediaGuid = mediaGuid,
                GroupName = groupName,
                CreatedBySubject = AuthConstants.FindSubject(User)
            },
            cancellationToken);

        return MapMutation(response);
    }

    [HttpDelete("media/{mediaGuid:guid}/groups/{groupName}")]
    [Endpoint(EndpointIds.MediaAccessMediaRemove)]
    [EndpointSummary("Stop allowing a group to watch a media item")]
    [EndpointDescription("Removes a group from a media item's playback allow-list. When the last group is removed, the item becomes unrestricted again.")]
    public async Task<IActionResult> RemoveMediaGroup(Guid mediaGuid, string groupName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.MediaRemove,
            new MediaAccessMediaMutateRequestMessage { MediaGuid = mediaGuid, GroupName = groupName },
            cancellationToken);

        return MapMutation(response);
    }

    [HttpDelete("media/{mediaGuid:guid}/groups")]
    [Endpoint(EndpointIds.MediaAccessMediaClear)]
    [EndpointSummary("Clear all watch restrictions on a media item")]
    [EndpointDescription("Removes every group from a media item's playback allow-list, making the item watchable by any user with media access.")]
    public async Task<IActionResult> ClearMediaGroups(Guid mediaGuid, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.MediaClear,
            new MediaAccessMediaListRequestMessage { MediaGuid = mediaGuid },
            cancellationToken);

        return MapMutation(response);
    }

    // --- Provider restrictions --------------------------------------------

    [HttpGet("providers")]
    [Endpoint(EndpointIds.MediaAccessProviderList)]
    [EndpointSummary("List provider watch restrictions")]
    [EndpointDescription("Returns every yt-dlp provider (extractor) that has a playback allow-list, with the groups permitted to watch its media. Providers not listed here are unrestricted.")]
    public async Task<IActionResult> ListProviders(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.ProviderList,
            new MediaAccessProviderListRequestMessage(),
            cancellationToken);

        return response is null
            ? Unavailable()
            : MapResult(response, () => Ok(response.Providers ?? []));
    }

    [HttpPost("providers/{provider}/groups/{groupName}")]
    [Endpoint(EndpointIds.MediaAccessProviderAdd)]
    [EndpointSummary("Allow a group to watch a provider's media")]
    [EndpointDescription("Adds a group to a provider's playback allow-list. Once any group is added, only members of an allowed group may watch media downloaded from that provider (e.g. restrict an adult site to an adults group).")]
    public async Task<IActionResult> AddProviderGroup(string provider, string groupName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.ProviderAdd,
            new MediaAccessProviderMutateRequestMessage
            {
                Provider = provider,
                GroupName = groupName,
                CreatedBySubject = AuthConstants.FindSubject(User)
            },
            cancellationToken);

        return MapMutation(response);
    }

    [HttpDelete("providers/{provider}/groups/{groupName}")]
    [Endpoint(EndpointIds.MediaAccessProviderRemove)]
    [EndpointSummary("Stop allowing a group to watch a provider's media")]
    [EndpointDescription("Removes a group from a provider's playback allow-list. When the last group is removed, the provider's media becomes unrestricted again.")]
    public async Task<IActionResult> RemoveProviderGroup(string provider, string groupName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.ProviderRemove,
            new MediaAccessProviderMutateRequestMessage { Provider = provider, GroupName = groupName },
            cancellationToken);

        return MapMutation(response);
    }

    [HttpDelete("providers/{provider}")]
    [Endpoint(EndpointIds.MediaAccessProviderClear)]
    [EndpointSummary("Clear all watch restrictions on a provider")]
    [EndpointDescription("Removes every group from a provider's playback allow-list, making media from that provider watchable by any user with media access.")]
    public async Task<IActionResult> ClearProvider(string provider, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.ProviderClear,
            new MediaAccessProviderMutateRequestMessage { Provider = provider },
            cancellationToken);

        return MapMutation(response);
    }

    // --- Age-limit policies -----------------------------------------------

    [HttpGet("age-policies")]
    [Endpoint(EndpointIds.MediaAccessAgeList)]
    [EndpointSummary("List age-limit watch policies")]
    [EndpointDescription("Returns the configured age-limit tiers and the groups allowed to watch media at or above each tier. Only the highest tier at or below a media item's reported age limit applies.")]
    public async Task<IActionResult> ListAgePolicies(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.AgeList,
            new MediaAccessAgeListRequestMessage(),
            cancellationToken);

        return response is null
            ? Unavailable()
            : MapResult(response, () => Ok(response.AgePolicies ?? []));
    }

    [HttpPost("age-policies")]
    [Endpoint(EndpointIds.MediaAccessAgeAdd)]
    [EndpointSummary("Allow a group to watch media at an age-limit tier")]
    [EndpointDescription("Adds a group to the allow-list for media whose reported age limit is at or above the supplied threshold (e.g. require an adults group for media rated 17+).")]
    public async Task<IActionResult> AddAgePolicy([FromBody] AgePolicyRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.AgeAdd,
            new MediaAccessAgeMutateRequestMessage
            {
                Threshold = request.Threshold,
                GroupName = request.GroupName,
                CreatedBySubject = AuthConstants.FindSubject(User)
            },
            cancellationToken);

        return MapMutation(response);
    }

    [HttpDelete("age-policies/{threshold:int}/groups/{groupName}")]
    [Endpoint(EndpointIds.MediaAccessAgeRemove)]
    [EndpointSummary("Stop allowing a group at an age-limit tier")]
    [EndpointDescription("Removes a group from the allow-list for an age-limit tier. When a tier has no remaining groups, it no longer restricts playback.")]
    public async Task<IActionResult> RemoveAgePolicy(int threshold, string groupName, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            MediaAccessSubjects.AgeRemove,
            new MediaAccessAgeMutateRequestMessage { Threshold = threshold, GroupName = groupName },
            cancellationToken);

        return MapMutation(response);
    }

    // --- Helpers -----------------------------------------------------------

    private async Task<MediaAccessOperationResponseMessage?> SendAsync<TRequest>(
        string subject,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, MediaAccessOperationResponseMessage>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed processing media-access request on subject '{Subject}'.", subject);
            return null;
        }
    }

    private IActionResult MapMutation(MediaAccessOperationResponseMessage? response)
        => response is null ? Unavailable() : MapResult(response, NoContent);

    private IActionResult MapResult(MediaAccessOperationResponseMessage response, Func<IActionResult> onSuccess)
        => response.Success
            ? onSuccess()
            : response.ErrorCode switch
            {
                "validation" => BadRequest(response.ErrorMessage),
                "not_found" => NotFound(response.ErrorMessage),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response.ErrorMessage ?? "Media access request failed.")
            };

    private IActionResult Unavailable()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
}

public sealed record AgePolicyRequest
{
    public int Threshold { get; init; }

    public string? GroupName { get; init; }
}
