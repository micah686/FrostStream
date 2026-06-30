using FlySwattr.NATS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Shared.Auth;
using Shared.Messaging;
using Shared.Secrets;
using WebAPI.Auth;
using WebAPI.Features.Notifications.Models;

namespace WebAPI.Features.Notifications.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController(
    IMessageBus messageBus,
    ISecretStore secretStore,
    ILogger<NotificationsController> logger) : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [HttpGet("preferences")]
    [Endpoint(EndpointIds.NotificationsPreferencesGet)]
    [EndpointSummary("Get notification preferences")]
    [EndpointDescription("Returns the authenticated user's notification settings, including provider metadata and event rules. Secret values are never returned; provider configuration contains only safe values and secret references.")]
    public async Task<ActionResult<NotificationPreferencesDto>> GetPreferences(CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<NotificationGetPreferencesRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.GetPreferences,
            new NotificationGetPreferencesRequestMessage { OwnerSubject = subject },
            "get notification preferences",
            cancellationToken);

        return ToPreferencesResult(response);
    }

    [HttpPut("preferences")]
    [Endpoint(EndpointIds.NotificationsPreferencesUpdate)]
    [EndpointSummary("Update notification preferences")]
    [EndpointDescription("Replaces the authenticated user's notification preference document. Provider secrets must remain in OpenBAO and may only be referenced through secret://providerKey/secretName placeholders.")]
    public async Task<ActionResult<NotificationPreferencesDto>> UpdatePreferences(
        [FromBody] NotificationPreferencesDto request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (NotificationProfileValidator.Validate(request) is { } validationError)
            return BadRequest(validationError);

        var response = await SendAsync<NotificationUpdatePreferencesRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.UpdatePreferences,
            new NotificationUpdatePreferencesRequestMessage { OwnerSubject = subject, Preferences = request },
            "update notification preferences",
            cancellationToken);

        return ToPreferencesResult(response);
    }

    [HttpGet("providers")]
    [Endpoint(EndpointIds.NotificationsProvidersList)]
    [EndpointSummary("List notification providers")]
    [EndpointDescription("Lists the authenticated user's notification providers with safe metadata and redacted configuration references only. Secret values and provider credentials are never included in the response.")]
    public async Task<ActionResult<IReadOnlyCollection<NotificationProviderDto>>> ListProviders(CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();

        var response = await SendAsync<NotificationGetPreferencesRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.GetPreferences,
            new NotificationGetPreferencesRequestMessage { OwnerSubject = subject },
            "list notification providers",
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return Ok((IReadOnlyCollection<NotificationProviderDto>)(response.Preferences?.Providers.ToArray() ?? []));
    }

    [HttpGet("providers/{providerKey}")]
    [Endpoint(EndpointIds.NotificationsProvidersGet)]
    [EndpointSummary("Get a notification provider")]
    [EndpointDescription("Returns one authenticated-user-owned notification provider profile by key. The provider JSON may include secret references, but stored API keys, tokens, and passwords are never returned.")]
    public async Task<ActionResult<NotificationProviderDto>> GetProvider(
        string providerKey,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (!SecretPaths.IsValidProfileKey(providerKey))
            return BadRequest("Provider key must match ^[a-z0-9-]{2,100}$.");

        var response = await SendAsync<NotificationGetPreferencesRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.GetPreferences,
            new NotificationGetPreferencesRequestMessage { OwnerSubject = subject },
            "get notification provider",
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        var provider = response.Preferences?.Providers.FirstOrDefault(x =>
            string.Equals(x.ProviderKey, providerKey, StringComparison.Ordinal));
        return provider is null ? NotFound("Notification provider was not found.") : Ok(provider);
    }

    [HttpPut("providers/{providerKey}")]
    [Endpoint(EndpointIds.NotificationsProvidersUpsert)]
    [EndpointSummary("Create or update a notification provider")]
    [EndpointDescription("Creates or replaces a user-owned notification provider metadata profile. The Notify configuration must be a typed provider envelope and may reference OpenBAO secrets only with secret://providerKey/secretName placeholders.")]
    public async Task<ActionResult<NotificationProviderDto>> UpsertProvider(
        string providerKey,
        [FromBody] NotificationProviderDto request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (!string.Equals(providerKey, request.ProviderKey, StringComparison.Ordinal))
            return BadRequest("Route providerKey must match request.providerKey.");
        if (NotificationProfileValidator.Validate(request) is { } validationError)
            return BadRequest(validationError);

        var response = await SendAsync<NotificationUpsertProviderRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.UpsertProvider,
            new NotificationUpsertProviderRequestMessage { OwnerSubject = subject, Provider = request },
            "upsert notification provider",
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);
        if (response.Provider is null)
            return StatusCode(StatusCodes.Status502BadGateway, "DataBridge returned an invalid provider response.");

        return Ok(response.Provider);
    }

    [HttpDelete("providers/{providerKey}")]
    [Endpoint(EndpointIds.NotificationsProvidersDelete)]
    [EndpointSummary("Delete a notification provider")]
    [EndpointDescription("Deletes the authenticated user's notification provider profile and removes the matching provider secret document from OpenBAO. Rules that referenced the provider are retained with that provider removed.")]
    public async Task<ActionResult<NotificationPreferencesDto>> DeleteProvider(
        string providerKey,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (!SecretPaths.IsValidProfileKey(providerKey))
            return BadRequest("Provider key must match ^[a-z0-9-]{2,100}$.");

        var response = await SendAsync<NotificationDeleteProviderRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.DeleteProvider,
            new NotificationDeleteProviderRequestMessage { OwnerSubject = subject, ProviderKey = providerKey },
            "delete notification provider",
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        await secretStore.DeleteAsync(SecretPaths.ForUserNotificationProvider(subject, providerKey), cancellationToken);
        return Ok(response.Preferences ?? new NotificationPreferencesDto());
    }

    [HttpPut("providers/{providerKey}/secrets")]
    [Endpoint(EndpointIds.NotificationsSecretsUpsert)]
    [EndpointSummary("Store notification provider secrets")]
    [EndpointDescription("Creates or replaces secret fields for one authenticated-user-owned notification provider in OpenBAO. Values are write-only and are referenced from provider JSON with secret://providerKey/secretName placeholders.")]
    public async Task<IActionResult> UpsertSecrets(
        string providerKey,
        [FromBody] NotificationSecretsUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (!SecretPaths.IsValidProfileKey(providerKey))
            return BadRequest("Provider key must match ^[a-z0-9-]{2,100}$.");
        if (request.Secrets.Count == 0)
            return BadRequest("At least one secret is required.");
        foreach (var secretName in request.Secrets.Keys)
        {
            if (!SecretPaths.IsValidNotificationSecretName(secretName))
                return BadRequest("Secret names must match ^[A-Za-z0-9_.-]{1,100}$.");
        }

        var path = SecretPaths.ForUserNotificationProvider(subject, providerKey);
        var existing = await secretStore.ReadAsync(path, cancellationToken);
        var merged = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing, StringComparer.Ordinal);
        foreach (var (key, value) in request.Secrets)
            merged[key] = value;

        await secretStore.WriteAsync(path, merged, cancellationToken);
        return NoContent();
    }

    [HttpDelete("providers/{providerKey}/secrets/{secretName}")]
    [Endpoint(EndpointIds.NotificationsSecretsDelete)]
    [EndpointSummary("Delete a notification provider secret")]
    [EndpointDescription("Deletes one write-only secret field from the authenticated user's notification provider secret document. If the last secret is removed, the provider secret document is deleted from OpenBAO.")]
    public async Task<IActionResult> DeleteSecret(
        string providerKey,
        string secretName,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (!SecretPaths.IsValidProfileKey(providerKey))
            return BadRequest("Provider key must match ^[a-z0-9-]{2,100}$.");
        if (!SecretPaths.IsValidNotificationSecretName(secretName))
            return BadRequest("Secret name must match ^[A-Za-z0-9_.-]{1,100}$.");

        var path = SecretPaths.ForUserNotificationProvider(subject, providerKey);
        var existing = await secretStore.ReadAsync(path, cancellationToken);
        if (existing is null || !existing.ContainsKey(secretName))
            return NotFound("Notification secret was not found.");

        var next = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        next.Remove(secretName);
        if (next.Count == 0)
            await secretStore.DeleteAsync(path, cancellationToken);
        else
            await secretStore.WriteAsync(path, next, cancellationToken);

        return NoContent();
    }

    [HttpPost("test")]
    [Endpoint(EndpointIds.NotificationsTest)]
    [EndpointSummary("Send a test notification")]
    [EndpointDescription("Sends a test message through one authenticated-user-owned notification provider. DataBridge expands secret references from OpenBAO at send time; stored secret values are never returned over HTTP.")]
    public async Task<IActionResult> Test(
        [FromBody] NotificationTestRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (ResolveSubject() is not { } subject)
            return Unauthorized();
        if (!string.Equals(request.OwnerSubject, subject, StringComparison.Ordinal))
            request = request with { OwnerSubject = subject };

        var response = await SendAsync<NotificationTestRequestMessage, NotificationOperationResponseMessage>(
            NotificationSubjects.Test,
            request,
            "send notification test",
            cancellationToken);

        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return NoContent();
    }

    private string? ResolveSubject()
    {
        var subject = AuthConstants.FindSubject(User);
        return SecretPaths.IsValidUserScope(subject) ? subject : null;
    }

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(
        string subject,
        TRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await messageBus.RequestAsync<TRequest, TResponse>(
                subject,
                request,
                RequestTimeout,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to {Operation}.", operation);
            return default;
        }
    }

    private ActionResult<NotificationPreferencesDto> ToPreferencesResult(NotificationOperationResponseMessage? response)
    {
        if (response is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "DataBridge is unreachable.");
        if (!response.Success)
            return MapError(response.ErrorCode, response.ErrorMessage);

        return Ok(response.Preferences ?? new NotificationPreferencesDto());
    }

    private ObjectResult MapError(string? errorCode, string? errorMessage)
    {
        var message = errorMessage ?? "Notification operation failed.";
        return errorCode switch
        {
            "not_found" => NotFound(message),
            "validation" => BadRequest(message),
            "disabled" => BadRequest(message),
            _ => StatusCode(StatusCodes.Status500InternalServerError, message)
        };
    }
}
