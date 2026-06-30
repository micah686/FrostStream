using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>
/// Serves the media access control surface: the server-to-server watch-time <c>check</c> gate plus the
/// per-media, per-provider, and age-limit administration subjects. All work is delegated to
/// <see cref="MediaAccessExecutor"/>.
/// </summary>
public sealed class MediaAccessConsumerService(
    IMessageBus messageBus,
    MediaAccessExecutor executor,
    ILogger<MediaAccessConsumerService> logger) : SubscriptionBackgroundService
{
    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<MediaAccessCheckRequestMessage>(messageBus, MediaAccessSubjects.Check, HandleCheckAsync, MediaAccessSubjects.QueueGroup, stoppingToken);

        await SubscribeAsync<MediaAccessMediaListRequestMessage>(messageBus, MediaAccessSubjects.MediaList, HandleMediaListAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessMediaMutateRequestMessage>(messageBus, MediaAccessSubjects.MediaAdd, HandleMediaAddAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessMediaMutateRequestMessage>(messageBus, MediaAccessSubjects.MediaRemove, HandleMediaRemoveAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessMediaListRequestMessage>(messageBus, MediaAccessSubjects.MediaClear, HandleMediaClearAsync, MediaAccessSubjects.QueueGroup, stoppingToken);

        await SubscribeAsync<MediaAccessProviderListRequestMessage>(messageBus, MediaAccessSubjects.ProviderList, HandleProviderListAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessProviderMutateRequestMessage>(messageBus, MediaAccessSubjects.ProviderAdd, HandleProviderAddAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessProviderMutateRequestMessage>(messageBus, MediaAccessSubjects.ProviderRemove, HandleProviderRemoveAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessProviderMutateRequestMessage>(messageBus, MediaAccessSubjects.ProviderClear, HandleProviderClearAsync, MediaAccessSubjects.QueueGroup, stoppingToken);

        await SubscribeAsync<MediaAccessAgeListRequestMessage>(messageBus, MediaAccessSubjects.AgeList, HandleAgeListAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessAgeMutateRequestMessage>(messageBus, MediaAccessSubjects.AgeAdd, HandleAgeAddAsync, MediaAccessSubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<MediaAccessAgeMutateRequestMessage>(messageBus, MediaAccessSubjects.AgeRemove, HandleAgeRemoveAsync, MediaAccessSubjects.QueueGroup, stoppingToken);

        logger.LogInformation("Subscribed to media access control subjects.");
    }

    // --- Watch-time gate ---------------------------------------------------

    private async Task HandleCheckAsync(IMessageContext<MediaAccessCheckRequestMessage> context)
    {
        try
        {
            var result = await executor.EvaluateAsync(context.Message.MediaGuid, context.Message.UserGroups);
            await context.RespondAsync(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed evaluating media access for {MediaGuid}.", context.Message.MediaGuid);
            // Fail closed: an evaluation error denies access rather than leaking restricted media.
            await context.RespondAsync(new MediaAccessCheckResponseMessage
            {
                IsAllowed = false,
                FailureReason = "evaluation-error",
                ErrorCode = "internal_error"
            });
        }
    }

    // --- Per-media ---------------------------------------------------------

    private async Task HandleMediaListAsync(IMessageContext<MediaAccessMediaListRequestMessage> context)
        => await RunListAsync(context, async () =>
            new MediaAccessOperationResponseMessage
            {
                Success = true,
                Groups = await executor.ListMediaGroupsAsync(context.Message.MediaGuid, CancellationToken.None)
            });

    private async Task HandleMediaAddAsync(IMessageContext<MediaAccessMediaMutateRequestMessage> context)
        => await RunMutateAsync(context, () => NormalizeGroup(context.Message.GroupName), async group =>
        {
            await executor.AddMediaGroupAsync(context.Message.MediaGuid, group, context.Message.CreatedBySubject, CancellationToken.None);
        });

    private async Task HandleMediaRemoveAsync(IMessageContext<MediaAccessMediaMutateRequestMessage> context)
        => await RunMutateAsync(context, () => NormalizeGroup(context.Message.GroupName), async group =>
        {
            await executor.RemoveMediaGroupAsync(context.Message.MediaGuid, group, CancellationToken.None);
        });

    private async Task HandleMediaClearAsync(IMessageContext<MediaAccessMediaListRequestMessage> context)
    {
        try
        {
            await executor.ClearMediaGroupsAsync(context.Message.MediaGuid, CancellationToken.None);
            await context.RespondAsync(new MediaAccessOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(context, ex, "clearing media restrictions");
        }
    }

    // --- Provider ----------------------------------------------------------

    private async Task HandleProviderListAsync(IMessageContext<MediaAccessProviderListRequestMessage> context)
        => await RunListAsync(context, async () =>
            new MediaAccessOperationResponseMessage
            {
                Success = true,
                Providers = await executor.ListProvidersAsync(CancellationToken.None)
            });

    private async Task HandleProviderAddAsync(IMessageContext<MediaAccessProviderMutateRequestMessage> context)
        => await RunProviderMutateAsync(context, async (provider, group) =>
        {
            await executor.AddProviderGroupAsync(provider, group, context.Message.CreatedBySubject, CancellationToken.None);
        });

    private async Task HandleProviderRemoveAsync(IMessageContext<MediaAccessProviderMutateRequestMessage> context)
        => await RunProviderMutateAsync(context, async (provider, group) =>
        {
            await executor.RemoveProviderGroupAsync(provider, group, CancellationToken.None);
        });

    private async Task HandleProviderClearAsync(IMessageContext<MediaAccessProviderMutateRequestMessage> context)
    {
        try
        {
            var provider = NormalizeProvider(context.Message.Provider);
            if (provider is null)
            {
                await context.RespondAsync(Failure("validation", "Provider is required."));
                return;
            }

            await executor.ClearProviderAsync(provider, CancellationToken.None);
            await context.RespondAsync(new MediaAccessOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(context, ex, "clearing provider restrictions");
        }
    }

    // --- Age ---------------------------------------------------------------

    private async Task HandleAgeListAsync(IMessageContext<MediaAccessAgeListRequestMessage> context)
        => await RunListAsync(context, async () =>
            new MediaAccessOperationResponseMessage
            {
                Success = true,
                AgePolicies = await executor.ListAgePoliciesAsync(CancellationToken.None)
            });

    private async Task HandleAgeAddAsync(IMessageContext<MediaAccessAgeMutateRequestMessage> context)
        => await RunAgeMutateAsync(context, async (threshold, group) =>
        {
            await executor.AddAgePolicyAsync(threshold, group, context.Message.CreatedBySubject, CancellationToken.None);
        });

    private async Task HandleAgeRemoveAsync(IMessageContext<MediaAccessAgeMutateRequestMessage> context)
        => await RunAgeMutateAsync(context, async (threshold, group) =>
        {
            await executor.RemoveAgePolicyAsync(threshold, group, CancellationToken.None);
        });

    // --- Shared plumbing ---------------------------------------------------

    private async Task RunListAsync<T>(IMessageContext<T> context, Func<Task<MediaAccessOperationResponseMessage>> action)
    {
        try
        {
            await context.RespondAsync(await action());
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(context, ex, "listing media access restrictions");
        }
    }

    private async Task RunMutateAsync<T>(IMessageContext<T> context, Func<string?> normalizeGroup, Func<string, Task> action)
    {
        try
        {
            var group = normalizeGroup();
            if (group is null)
            {
                await context.RespondAsync(Failure("validation", "Group name is required."));
                return;
            }

            await action(group);
            await context.RespondAsync(new MediaAccessOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(context, ex, "updating media access restrictions");
        }
    }

    private async Task RunProviderMutateAsync(
        IMessageContext<MediaAccessProviderMutateRequestMessage> context,
        Func<string, string, Task> action)
    {
        try
        {
            var provider = NormalizeProvider(context.Message.Provider);
            var group = NormalizeGroup(context.Message.GroupName);
            if (provider is null || group is null)
            {
                await context.RespondAsync(Failure("validation", "Provider and group name are required."));
                return;
            }

            await action(provider, group);
            await context.RespondAsync(new MediaAccessOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(context, ex, "updating provider restrictions");
        }
    }

    private async Task RunAgeMutateAsync(
        IMessageContext<MediaAccessAgeMutateRequestMessage> context,
        Func<int, string, Task> action)
    {
        try
        {
            var group = NormalizeGroup(context.Message.GroupName);
            if (group is null)
            {
                await context.RespondAsync(Failure("validation", "Group name is required."));
                return;
            }

            if (context.Message.Threshold < 0)
            {
                await context.RespondAsync(Failure("validation", "Age threshold must be zero or greater."));
                return;
            }

            await action(context.Message.Threshold, group);
            await context.RespondAsync(new MediaAccessOperationResponseMessage { Success = true });
        }
        catch (Exception ex)
        {
            await RespondErrorAsync(context, ex, "updating age policies");
        }
    }

    private async Task RespondErrorAsync<T>(IMessageContext<T> context, Exception ex, string action)
    {
        logger.LogError(ex, "Failed {Action}.", action);
        await context.RespondAsync(Failure("internal_error", "Internal media access service error."));
    }

    private static string? NormalizeGroup(string? group)
        => string.IsNullOrWhiteSpace(group) ? null : group.Trim();

    private static string? NormalizeProvider(string? provider)
        => string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();

    private static MediaAccessOperationResponseMessage Failure(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}
