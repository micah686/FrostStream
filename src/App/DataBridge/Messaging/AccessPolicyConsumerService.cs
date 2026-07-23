using Conduit.NATS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Shared.Messaging;
using System.Text.RegularExpressions;

namespace DataBridge.Messaging;

public sealed partial class AccessPolicyConsumerService(
    IMessageBus messageBus,
    AccessPolicyExecutor executor,
    IOptions<MediaAccessOptions> mediaAccessOptions,
    ILogger<AccessPolicyConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<AccessPolicyListRequestMessage>(messageBus, AccessPolicySubjects.List, HandleListAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicyGetRequestMessage>(messageBus, AccessPolicySubjects.Get, HandleGetAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicySaveRequestMessage>(messageBus, AccessPolicySubjects.Save, HandleSaveAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicyDeleteRequestMessage>(messageBus, AccessPolicySubjects.Delete, HandleDeleteAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicySetSyncRequestMessage>(messageBus, AccessPolicySubjects.SetSync, HandleSetSyncAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicyListRequestMessage>(messageBus, AccessPolicySubjects.ProviderCatalog, HandleProvidersAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicyMediaSummaryRequestMessage>(messageBus, AccessPolicySubjects.MediaSummary, HandleMediaSummaryAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<AccessPolicyEffectiveMediaRequestMessage>(messageBus, AccessPolicySubjects.EffectiveMedia, HandleEffectiveMediaAsync, AccessPolicySubjects.QueueGroup, stoppingToken);
    }

    private Task HandleListAsync(IMessageContext<AccessPolicyListRequestMessage> context)
        => RespondAsync(context, async () => new AccessPolicyOperationResponseMessage
        {
            Success = true,
            Policies = await executor.ListAsync(CancellationToken.None)
        });

    private Task HandleGetAsync(IMessageContext<AccessPolicyGetRequestMessage> context)
        => RespondAsync(context, async () =>
        {
            var policy = await executor.GetAsync(context.Message.PolicyId, CancellationToken.None);
            return policy is null
                ? Failure("not_found", "Access policy was not found.")
                : new AccessPolicyOperationResponseMessage { Success = true, Policy = policy };
        });

    private Task HandleSaveAsync(IMessageContext<AccessPolicySaveRequestMessage> context)
        => RespondAsync(context, async () =>
        {
            var validation = Validate(context.Message.Policy);
            if (validation is not null)
            {
                return Failure("validation", validation);
            }

            try
            {
                var saved = await executor.SaveAsync(Normalize(context.Message.Policy), CancellationToken.None);
                return new AccessPolicyOperationResponseMessage { Success = true, Policy = saved };
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure("validation", "An access policy with this name already exists.");
            }
        });

    private Task HandleDeleteAsync(IMessageContext<AccessPolicyDeleteRequestMessage> context)
        => RespondAsync(context, async () => await executor.DeleteAsync(context.Message.PolicyId, CancellationToken.None)
            ? new AccessPolicyOperationResponseMessage { Success = true }
            : Failure("not_found", "Access policy was not found."));

    private Task HandleSetSyncAsync(IMessageContext<AccessPolicySetSyncRequestMessage> context)
        => RespondAsync(context, async () =>
        {
            var policy = await executor.SetSyncAsync(
                context.Message.PolicyId, context.Message.Version, context.Message.Status,
                context.Message.Error, CancellationToken.None);
            return policy is null
                ? Failure("not_found", "Access policy was not found.")
                : new AccessPolicyOperationResponseMessage { Success = true, Policy = policy };
        });

    private Task HandleProvidersAsync(IMessageContext<AccessPolicyListRequestMessage> context)
        => RespondAsync(context, async () => new AccessPolicyOperationResponseMessage
        {
            Success = true,
            Providers = await executor.ListProviderCatalogAsync(CancellationToken.None)
        });

    private Task HandleMediaSummaryAsync(IMessageContext<AccessPolicyMediaSummaryRequestMessage> context)
        => RespondAsync(context, async () => new AccessPolicyOperationResponseMessage
        {
            Success = true,
            MediaSummary = await executor.GetMediaSummaryAsync(context.Message.MediaGuid, CancellationToken.None)
        });

    private Task HandleEffectiveMediaAsync(IMessageContext<AccessPolicyEffectiveMediaRequestMessage> context)
        => RespondAsync(context, async () => new AccessPolicyOperationResponseMessage
        {
            Success = true,
            EffectiveMedia = await executor.EvaluateAsync(
                context.Message.MediaGuid,
                context.Message.UserSubject,
                context.Message.UserGroups,
                mediaAccessOptions.Value.AdminBypassGroups,
                CancellationToken.None)
        });

    private async Task RespondAsync<T>(IMessageContext<T> context, Func<Task<AccessPolicyOperationResponseMessage>> action)
    {
        try
        {
            await context.RespondAsync(await action());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Access-policy request failed.");
            await context.RespondAsync(Failure("internal_error", "Internal access-policy service error."));
        }
    }

    internal static string? Validate(AccessPolicyDto policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name))
            return "Policy name is required.";
        if (policy.Name.Trim().Length > 200)
            return "Policy name cannot exceed 200 characters.";
        if (policy.Description?.Trim().Length > 2000)
            return "Policy description cannot exceed 2000 characters.";
        if (policy.BundleIds.Count + policy.MediaGuids.Count + policy.Providers.Count + policy.AgeThresholds.Count == 0)
            return "A policy must reference at least one bundle, media GUID, provider, or age threshold.";
        if (policy.BundleIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Trim().Length > 255))
            return "Bundle ids must be between 1 and 255 characters.";
        if (policy.Providers.Any(x => string.IsNullOrWhiteSpace(x) || x.Trim().Length > 255))
            return "Providers must be between 1 and 255 characters.";
        if (policy.AgeThresholds.Any(x => x < 0))
            return "Age thresholds must be zero or greater.";
        if (policy.Assignments.Any(x =>
                x.Type.Trim().ToLowerInvariant() is not ("user" or "group") ||
                string.IsNullOrWhiteSpace(x.Id) ||
                x.Id.Trim().Length > 255 ||
                !ValidPrincipalIdRegex().IsMatch(x.Id.Trim())))
            return "Every assignment must identify a user or group.";
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.@/+=,|-]+$")]
    private static partial Regex ValidPrincipalIdRegex();

    private static AccessPolicyDto Normalize(AccessPolicyDto policy) => policy with
    {
        Name = policy.Name.Trim(),
        Description = string.IsNullOrWhiteSpace(policy.Description) ? null : policy.Description.Trim(),
        BundleIds = policy.BundleIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Order().ToArray(),
        MediaGuids = policy.MediaGuids.Distinct().Order().ToArray(),
        Providers = policy.Providers.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order().ToArray(),
        AgeThresholds = policy.AgeThresholds.Distinct().Order().ToArray(),
        Assignments = policy.Assignments
            .Select(x => x with { Type = x.Type.Trim().ToLowerInvariant(), Id = x.Id.Trim(), DisplayName = null })
            .DistinctBy(x => (x.Type, x.Id))
            .OrderBy(x => x.Type).ThenBy(x => x.Id).ToArray()
    };

    private static AccessPolicyOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}
