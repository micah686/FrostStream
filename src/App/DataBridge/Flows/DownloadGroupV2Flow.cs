using System.Security.Cryptography;
using System.Text;
using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using Conduit.NATS;
using DataBridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Downloads;
using Shared.Messaging;

namespace DataBridge.Flows;

/// <summary>
/// Durable coordinator for one direct request or one collection expansion. The group flow
/// never downloads media itself; it creates independent immutable child runs so one child
/// failure cannot terminate its siblings.
/// </summary>
[GenerateFlows]
public sealed class DownloadGroupV2Flow(
    IJetStreamPublisher publisher,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<DownloadGroupV2Flow> logger) : Flow<DownloadGroupRequested>
{
    private const int MaxExpansionAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public override async Task Run(DownloadGroupRequested request)
    {
        await Capture(() => V2(r => r.SetGroupStatusAsync(request.GroupId, DownloadGroupStatus.Expanding)));

        if (request.Kind == DownloadGroupKind.Direct)
        {
            await RunDirectAsync(request);
            return;
        }

        if (request.Kind is DownloadGroupKind.Channel or DownloadGroupKind.CreatorMonitor
            && request.ChannelRequest is not null)
        {
            await RunChannelAsync(request, request.ChannelRequest);
            return;
        }

        if (request.Kind != DownloadGroupKind.Playlist)
            throw new InvalidOperationException($"Unsupported download group kind '{request.Kind}'.");

        var collection = request.CollectionRequest
                         ?? throw new InvalidOperationException("A collection group requires CollectionRequest.");
        await RunCollectionAsync(request, collection);
    }

    private async Task RunChannelAsync(DownloadGroupRequested group, ChannelMediaListRequested original)
    {
        for (var attempt = 1; attempt <= MaxExpansionAttempts; attempt++)
        {
            if (!await Capture(() => V2(r => r.IsGroupExpansionAllowedAsync(group.CorrelationId))))
                return;

            var dispatchId = await Capture(Guid.NewGuid);
            var command = original with
            {
                GroupId = group.GroupId,
                CorrelationId = group.CorrelationId,
                ExpansionDispatchId = dispatchId,
                ExpansionAttempt = attempt,
                IdempotencyKey = $"{original.IdempotencyKey}:group:{group.GroupId:N}:attempt:{attempt}",
                OccurredAt = clock.GetCurrentInstant()
            };
            await Capture(() => publisher.PublishAsync(
                BackgroundJobSubjects.ChannelMediaListRequest,
                command,
                messageId: command.IdempotencyKey));

            var result = await Messages
                .OfTypes<DownloadGroupExpansionSucceeded, DownloadGroupExpansionFailed, DownloadGroupStopRequested>()
                .First();
            if (result.HasThird)
            {
                await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Stopped)));
                return;
            }
            if (result.HasFirst)
            {
                await Capture(() => V2(r => r.RefreshGroupAggregateAsync(group.CorrelationId)));
                if (result.First.ExpectedJobs == 0)
                    await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Completed)));
                else
                    await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Running)));
                return;
            }

            var failure = result.Second;
            if (Retryable(failure.FailureKind) && attempt < MaxExpansionAttempts)
            {
                await Capture(() => Task.Delay(RetryDelay));
                continue;
            }
            await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Failed,
                failure.ErrorCode ?? "channel_expansion_failed", failure.ErrorMessage)));
            return;
        }
    }

    private async Task RunDirectAsync(DownloadGroupRequested request)
    {
        var child = request.DirectRequest
                    ?? throw new InvalidOperationException("A direct group requires DirectRequest.");
        // Start the child through the ordinary ingress consumer. Calling Run inline shares the
        // group's effect scope, while Schedule implicitly attaches the child to this short-lived
        // group flow. Publishing here makes the job flow an independent durable root, exactly as
        // collection fan-out jobs are started below.
        await Capture(() => publisher.PublishAsync(
            DownloadSubjects.DownloadRequested,
            child,
            messageId: child.MessageId.ToString("N")));
    }

    private async Task RunCollectionAsync(DownloadGroupRequested group, PlaylistRequested collection)
    {
        var playlistId = await Capture(() => Playlists(async r =>
            (await r.CreateOrReuseAsync(collection)).Playlist.PlaylistId));
        var pageStart = FetchPlaylistMetadataCommandDefaults.PageStartIndex;
        var pageSize = FetchPlaylistMetadataCommandDefaults.PageSize;

        while (true)
        {
            var page = await FetchCollectionPageAsync(group, collection, playlistId, pageStart, pageSize);
            if (page is null)
                return;

            await Capture(() => Playlists(r => r.ApplyMetadataFetchedAsync(playlistId, page)));
            await Capture(() => Playlists(r => r.WriteStagingEntriesAsync(playlistId, page.Entries)));
            if (page.IsComplete)
                break;

            var nextPage = page.NextPageStartIndex
                           ?? page.PageStartIndex + Math.Max(page.PageSize, page.Entries.Count);
            if (nextPage <= pageStart)
            {
                await FailExpansionAsync(group, playlistId, "invalid_collection_page",
                    $"Collection expansion returned non-advancing page index {nextPage}.");
                return;
            }
            pageStart = nextPage;
        }

        if (!await Capture(() => V2(r => r.IsGroupExpansionAllowedAsync(group.CorrelationId))))
            return;

        var entries = await Capture(() => Playlists(r => r.ListStagingEntriesAsync(playlistId)));
        var ignoreKeywords = await Capture(() => LoadIgnoreKeywordsAsync(
            collection.RequestedBy, collection.ConfigSetKey));

        foreach (var entry in entries)
        {
            if (!await Capture(() => V2(r => r.IsGroupExpansionAllowedAsync(group.CorrelationId))))
            {
                await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Stopped)));
                return;
            }

            var jobId = DeterministicJobId(playlistId, entry.PlaylistIndex, entry.EntryUrl);
            var ignoreMatch = IgnoreKeywordMatcher.FirstMatch(entry.EntryTitle, ignoreKeywords);
            await Capture(() => Playlists(r => r.FanOutEntryAsync(new FanOutEntryRequest(
                PlaylistId: playlistId,
                CorrelationId: group.CorrelationId,
                JobId: jobId,
                PlaylistIndex: entry.PlaylistIndex,
                EntryUrl: entry.EntryUrl,
                EntryTitle: entry.EntryTitle,
                RequestedBy: collection.RequestedBy,
                StorageKey: collection.StorageKey,
                InitialState: ignoreMatch is null ? DownloadJobState.Queued : DownloadJobState.Ignored,
                IgnoredKeyword: ignoreMatch?.Pattern))));

            if (ignoreMatch is not null)
                continue;

            var messageId = DeterministicMessageId(group.GroupId, jobId);
            var child = new DownloadRequested
            {
                JobId = jobId,
                CorrelationId = group.CorrelationId,
                CausationId = group.MessageId,
                MessageId = messageId,
                OperationKey = $"group/{group.GroupId:N}/job/{jobId:N}/requested",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = 1,
                SourceUrl = entry.EntryUrl,
                RequestedBy = collection.RequestedBy,
                StorageKey = collection.StorageKey ?? "default",
                YtDlpOptions = collection.YtDlpOptions,
                CookieSecretPath = collection.CookieSecretPath,
                Priority = collection.Priority,
                FetchComments = collection.FetchComments,
                EncodeAudioRendition = collection.EncodeForPlaylist,
                SourceKind = group.Kind == DownloadGroupKind.Playlist
                    ? DownloadSourceKind.Playlist
                    : DownloadSourceKind.Channel
            };
            await Capture(() => publisher.PublishAsync(
                DownloadSubjects.DownloadRequested,
                child,
                messageId: child.MessageId.ToString("N")));
        }

        await Capture(() => V2(r => r.RefreshGroupAggregateAsync(group.CorrelationId)));
        if (entries.Count == 0)
            await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Completed)));

        logger.LogInformation(
            "Download group {GroupId} expanded {Count} collection entries into independent jobs.",
            group.GroupId, entries.Count);
    }

    private async Task<PlaylistMetadataFetched?> FetchCollectionPageAsync(
        DownloadGroupRequested group,
        PlaylistRequested collection,
        Guid playlistId,
        int pageStart,
        int pageSize)
    {
        for (var attempt = 1; attempt <= MaxExpansionAttempts; attempt++)
        {
            if (!await Capture(() => V2(r => r.IsGroupExpansionAllowedAsync(group.CorrelationId))))
                return null;

            var dispatchId = await Capture(Guid.NewGuid);
            var command = new FetchPlaylistMetadataCommand
            {
                PlaylistId = playlistId,
                CorrelationId = group.CorrelationId,
                CausationId = group.MessageId,
                MessageId = dispatchId,
                OperationKey = $"group/{group.GroupId:N}/expand/page/{pageStart}/attempt/{attempt}",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                SourceUrl = collection.SourceUrl,
                PageStartIndex = pageStart,
                PageSize = pageSize
            };
            await Capture(() => publisher.PublishAsync(
                PlaylistSubjects.FetchPlaylistMetadataCommand,
                command,
                messageId: command.MessageId.ToString("N")));

            var result = await Messages
                .OfTypes<PlaylistMetadataFetched, PlaylistMetadataFetchFailed, DownloadGroupStopRequested>()
                .First();
            if (result.HasThird)
            {
                await Capture(() => V2(r => r.SetGroupStatusAsync(group.GroupId, DownloadGroupStatus.Stopped)));
                return null;
            }
            if (result.HasFirst)
                return result.First;

            var failure = result.Second;
            if (Retryable(failure.FailureKind) && attempt < MaxExpansionAttempts)
            {
                await Capture(() => Task.Delay(RetryDelay));
                continue;
            }

            await FailExpansionAsync(group, playlistId,
                failure.ErrorCode ?? "collection_expansion_failed", failure.ErrorMessage);
            return null;
        }
        return null;
    }

    private async Task FailExpansionAsync(
        DownloadGroupRequested group, Guid playlistId, string code, string message)
    {
        await Capture(() => Playlists(r => r.UpdateStateAsync(playlistId, PlaylistState.Failed)));
        await Capture(() => V2(r => r.SetGroupStatusAsync(
            group.GroupId, DownloadGroupStatus.Failed, code, message)));
    }

    private async Task<IReadOnlyList<IgnoreKeyword>> LoadIgnoreKeywordsAsync(
        string? ownerSubject, string? configSetKey)
    {
        if (string.IsNullOrWhiteSpace(ownerSubject) || string.IsNullOrWhiteSpace(configSetKey))
            return [];
        var configSet = await ConfigSets(r => r.GetAsync(ownerSubject, configSetKey));
        return IgnoreKeywordMatcher.Deserialize(configSet?.IgnoreKeywordsJson);
    }

    private static bool Retryable(FailureKind kind)
        => kind is FailureKind.Unknown or FailureKind.Transient or FailureKind.Timeout;

    private static Guid DeterministicJobId(Guid playlistId, int playlistIndex, string entryUrl)
        => HashGuid($"job:{playlistId:N}:{playlistIndex}:{entryUrl}");

    private static Guid DeterministicMessageId(Guid groupId, Guid jobId)
        => HashGuid($"message:{groupId:N}:{jobId:N}");

    private static Guid HashGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private Task V2(Func<IDownloadFlowV2Repository, Task> action) => scopeFactory.WithScopedAsync(action);
    private Task<T> V2<T>(Func<IDownloadFlowV2Repository, Task<T>> action) => scopeFactory.WithScopedAsync(action);
    private Task Playlists(Func<IPlaylistsRepository, Task> action) => scopeFactory.WithScopedAsync(action);
    private Task<T> Playlists<T>(Func<IPlaylistsRepository, Task<T>> action) => scopeFactory.WithScopedAsync(action);
    private Task<T> ConfigSets<T>(Func<IDownloadConfigSetsRepository, Task<T>> action) => scopeFactory.WithScopedAsync(action);
}
