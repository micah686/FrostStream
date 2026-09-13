using System.Buffers;
using System.IO.Hashing;
using System.Text.Json;
using DataBridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Application;
using Shared.Database;
using Shared.Messaging;
using Shared.Storage;
using Shared.Secrets;
using YtDlpSharpLib;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;

namespace DataBridge.Lite;

public interface ILiteDiscoveryAndImportIngress
{
    Task<DurableWorkReceipt> AcceptPlaylistAsync(LitePlaylistSubmission request, string owner, CancellationToken ct = default);
    Task<DurableWorkReceipt> AcceptCreatorScanAsync(LiteCreatorScanSubmission request, string owner, CancellationToken ct = default);
    Task<(Guid SessionId, DurableWorkReceipt Receipt)> AcceptImportScanAsync(LiteImportScanSubmission request, CancellationToken ct = default);
    Task<IReadOnlyList<DurableWorkReceipt>> CommitImportAsync(Guid sessionId, CancellationToken ct = default);
}

public sealed class LiteDiscoveryAndImportIngress(
    IServiceScopeFactory scopeFactory,
    ILocalExecutionStore store,
    ILocalExecutionWakeSignal wake,
    IClock clock) : ILiteDiscoveryAndImportIngress
{
    private static readonly JsonSerializerOptions JsonOptions = LiteDownloadIngress.CreateJsonOptions();

    public async Task<DurableWorkReceipt> AcceptPlaylistAsync(LitePlaylistSubmission request, string owner, CancellationToken ct = default)
    {
        if (!AbsoluteHttp(request.SourceUrl)) throw new ArgumentException("SourceUrl must be an absolute HTTP(S) URL.");
        var playlistId = Guid.NewGuid(); var correlationId = Guid.NewGuid();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>().CreateOrReuseAsync(new PlaylistRequested
            {
                PlaylistId = playlistId, CorrelationId = correlationId, MessageId = Guid.NewGuid(),
                OperationKey = $"lite/playlist/{playlistId:N}", OccurredAt = clock.GetCurrentInstant(),
                SourceUrl = request.SourceUrl, RequestedBy = owner, StorageKey = request.StorageKey ?? "default",
                EncodeForPlaylist = request.EncodeForPlaylist, Priority = request.Priority,
                FetchComments = request.FetchComments, YtDlpOptions = request.YtDlpOptions
            }, ct);
            playlistId = created.Playlist.PlaylistId;
            correlationId = created.Playlist.CorrelationId;
        }
        var item = await store.EnqueueAsync(LiteAcquisitionKinds.PlaylistExpansion, $"playlist:{playlistId:N}",
            JsonSerializer.SerializeToElement(new LitePlaylistWork
            {
                PlaylistId = playlistId, CorrelationId = correlationId, SourceUrl = request.SourceUrl,
                RequestedBy = owner, StorageKey = request.StorageKey ?? "default", CookieProfileKey = request.CookieProfileKey,
                EncodeForPlaylist = request.EncodeForPlaylist, Priority = request.Priority,
                FetchComments = request.FetchComments, YtDlpOptions = request.YtDlpOptions
            }, JsonOptions), 3, ct);
        wake.Wake(); return Receipt(item);
    }

    public async Task<DurableWorkReceipt> AcceptCreatorScanAsync(LiteCreatorScanSubmission request, string owner, CancellationToken ct = default)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
            if (await scope.ServiceProvider.GetRequiredService<ICreatorDiscoveryRepository>().GetSourceAsync(request.SourceId, ct) is null)
                throw new ArgumentException($"Creator source '{request.SourceId}' was not found.");
        var key = $"creator:{request.SourceId}:{request.ScanMode}:{clock.GetCurrentInstant().ToUnixTimeSeconds() / 60}";
        var item = await store.EnqueueAsync(LiteAcquisitionKinds.CreatorScan, key,
            JsonSerializer.SerializeToElement(new LiteCreatorScanWork
            {
                SourceId = request.SourceId, ScanMode = request.ScanMode, StorageKey = request.StorageKey,
                CookieProfileKey = request.CookieProfileKey, QueueDiscovered = request.QueueDiscovered, RequestedBy = owner
            }, JsonOptions), 3, ct);
        wake.Wake(); return Receipt(item);
    }

    public async Task<(Guid SessionId, DurableWorkReceipt Receipt)> AcceptImportScanAsync(LiteImportScanSubmission request, CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid();
        await using (var scope = scopeFactory.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IImportSessionRepository>().CreateAsync(new ImportSessionCreateRequest
            {
                SourceKind = ImportSessionSourceKind.WorkerIncoming, SubPath = request.SubPath,
                StorageKey = request.StorageKey
            }, sessionId, Guid.NewGuid(), ct);
        var item = await store.EnqueueAsync(LiteAcquisitionKinds.LocalImport, $"import-scan:{sessionId:N}",
            JsonSerializer.SerializeToElement(new LiteImportScanWork
            { SessionId = sessionId, SubPath = request.SubPath, StorageKey = request.StorageKey }, JsonOptions), 3, ct);
        wake.Wake(); return (sessionId, Receipt(item));
    }

    public async Task<IReadOnlyList<DurableWorkReceipt>> CommitImportAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var (_, _, error) = await repository.CommitAsync(sessionId, ct);
        if (error is not null) throw new InvalidOperationException(error);
        var work = await repository.ClaimApprovedWorkAsync(sessionId, 5000, ct);
        var receipts = new List<DurableWorkReceipt>(work.Count);
        foreach (var entry in work)
        {
            var item = await store.EnqueueAsync(LiteAcquisitionKinds.LocalImport,
                $"import-item:{sessionId:N}:{entry.ItemId:N}",
                JsonSerializer.SerializeToElement(new LiteImportItemWork { SessionId = sessionId, ItemId = entry.ItemId }, JsonOptions), 3, ct);
            receipts.Add(Receipt(item));
        }
        wake.Wake(); return receipts;
    }

    private static DurableWorkReceipt Receipt(LocalExecutionItem item) => new(DurableWorkDisposition.Accepted, item.WorkId, item.DeduplicationKey);
    private static bool AbsoluteHttp(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public sealed class LitePlaylistExecutionHandler(
    IServiceScopeFactory scopeFactory, IYtDlpClient ytDlp, ILitePotProvider pot,
    ILocalExecutionStore store, ILocalExecutionWakeSignal wake, IClock clock,
    ILogger<LitePlaylistExecutionHandler> logger) : ILocalExecutionHandler, IPlaylistExpansionExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = LiteDownloadIngress.CreateJsonOptions();
    public string Kind => LiteAcquisitionKinds.PlaylistExpansion;
    public async Task<WorkExecutionResult> ExecuteAsync(LocalExecutionItem item, CancellationToken ct)
    {
        var work = item.Payload.Deserialize<LitePlaylistWork>(JsonOptions)
                   ?? throw new InvalidOperationException("Playlist work payload is empty.");
        try
        {
            await pot.EnsureAvailableAsync(ct);
            var options = pot.Apply(new YtDlpOptions
            { VideoSelection = new YtDlpVideoSelectionOptions { PlaylistItems = "1:5000" } });
            var result = await ytDlp.TryGetVideoInfoAsync(work.SourceUrl, ct, flat: true, overrideOptions: options);
            if (!result.Success || result.Data is not { } container)
                throw new InvalidOperationException(result.ErrorOutput ?? "yt-dlp playlist metadata failed.");
            var entries = (container.Entries ?? [])
                .Select((entry, index) => ToPlaylistEntry(work.SourceUrl, entry, index + 1))
                .Where(x => x is not null).Cast<PlaylistEntry>().Take(5000).ToArray();
            if (entries.Length == 0) throw new InvalidOperationException("The provider returned no playlist entries.");
            var fetched = new PlaylistMetadataFetched
            {
                PlaylistId = work.PlaylistId, CorrelationId = work.CorrelationId, MessageId = Guid.NewGuid(),
                OperationKey = $"lite/playlist/{work.PlaylistId:N}/metadata", OccurredAt = clock.GetCurrentInstant(), Attempt = item.Attempt,
                ProviderPlaylistId = container.PlaylistId ?? container.Id, Title = container.PlaylistTitle ?? container.Title,
                TotalItems = container.PlaylistCount ?? entries.Length, Entries = entries
            };
            await using var scope = scopeFactory.CreateAsyncScope();
            var playlists = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();
            await playlists.ApplyMetadataFetchedAsync(work.PlaylistId, fetched, ct);
            await playlists.WriteStagingEntriesAsync(work.PlaylistId, entries, ct);
            foreach (var entry in entries)
            {
                var jobId = Guid.NewGuid();
                await playlists.FanOutEntryAsync(new FanOutEntryRequest(work.PlaylistId, work.CorrelationId, jobId,
                    entry.PlaylistIndex, entry.EntryUrl, entry.EntryTitle, work.RequestedBy, work.StorageKey), ct);
                var request = new DownloadRequested
                {
                    JobId = jobId, CorrelationId = work.CorrelationId, MessageId = Guid.NewGuid(),
                    OperationKey = $"lite/playlist/{work.PlaylistId:N}/item/{entry.PlaylistIndex}", OccurredAt = clock.GetCurrentInstant(),
                    SourceUrl = entry.EntryUrl, RequestedBy = work.RequestedBy, StorageKey = work.StorageKey,
                    SourceKind = DownloadSourceKind.Playlist, EncodeAudioRendition = work.EncodeForPlaylist,
                    Priority = work.Priority, FetchComments = work.FetchComments, YtDlpOptions = work.YtDlpOptions
                };
                var run = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>().CreateInitialRunAsync(request, true, ct);
                if (run is null) continue;
                await store.EnqueueAsync(LiteAcquisitionKinds.Download, $"download:{jobId:N}:{run.RunId:N}",
                    JsonSerializer.SerializeToElement(new LiteDownloadWork
                    {
                        JobId = jobId, RunId = run.RunId, CorrelationId = work.CorrelationId,
                        SourceUrl = entry.EntryUrl, StorageKey = work.StorageKey, MediaKind = MediaKind.Video,
                        SourceKind = DownloadSourceKind.Playlist,
                        CookieSecretPath = string.IsNullOrWhiteSpace(work.CookieProfileKey) ? null
                            : SecretPaths.ForUserCookieProfile(work.RequestedBy, work.CookieProfileKey),
                        EncodeAudioRendition = work.EncodeForPlaylist, FetchComments = work.FetchComments,
                        Priority = work.Priority, YtDlpOptions = work.YtDlpOptions
                    }, JsonOptions), 3, ct);
            }
            wake.Wake();
            return new WorkExecutionResult(WorkExecutionDisposition.Completed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Local playlist expansion {PlaylistId} failed.", work.PlaylistId);
            if (item.Attempt < item.MaximumAttempts) return new(WorkExecutionDisposition.Retry, "playlist_failed", exception.Message);
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>().UpdateStateAsync(work.PlaylistId, PlaylistState.Failed, ct);
            return new(WorkExecutionDisposition.Failed, "playlist_failed", exception.Message);
        }
    }

    Task<WorkExecutionResult> ILocalWorkExecutor<IPlaylistFlowMessage>.ExecuteAsync(IPlaylistFlowMessage work, CancellationToken ct)
        => Task.FromResult(new WorkExecutionResult(WorkExecutionDisposition.Failed, "local_payload_required"));

    private static PlaylistEntry? ToPlaylistEntry(string collectionUrl, VideoInfo entry, int fallback)
    {
        var url = IsYouTube(collectionUrl) && !string.IsNullOrWhiteSpace(entry.Id)
            ? $"https://www.youtube.com/watch?v={Uri.EscapeDataString(entry.Id)}"
            : new[] { entry.WebpageUrl, entry.Url }.FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out _)
                && !string.Equals(x?.TrimEnd('/'), collectionUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(url) ? null : new PlaylistEntry
        { PlaylistIndex = entry.PlaylistIndex ?? fallback, EntryUrl = url, EntryTitle = entry.Title ?? entry.FullTitle };
    }
    private static bool IsYouTube(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase));
}

public sealed class LiteCreatorScanExecutionHandler(
    IServiceScopeFactory scopeFactory, IYtDlpClient ytDlp, ILitePotProvider pot, ILiteDownloadIngress downloadIngress, IClock clock,
    ILogger<LiteCreatorScanExecutionHandler> logger) : ILocalExecutionHandler, ICreatorScanExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = LiteDownloadIngress.CreateJsonOptions();
    public string Kind => LiteAcquisitionKinds.CreatorScan;
    public async Task<WorkExecutionResult> ExecuteAsync(LocalExecutionItem item, CancellationToken ct)
    {
        var work = item.Payload.Deserialize<LiteCreatorScanWork>(JsonOptions)!;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ICreatorDiscoveryRepository>();
            var source = await repository.GetSourceAsync(work.SourceId, ct) ?? throw new InvalidOperationException("Creator source was deleted.");
            var storageKey = work.StorageKey ?? "default";
            var cookieProfileKey = work.CookieProfileKey;
            var priority = 0;
            var encodeForPlaylist = false;
            var fetchComments = false;
            YtDlpOptions? configuredYtDlp = null;
            if (!string.IsNullOrWhiteSpace(source.Source.ConfigSetKey))
            {
                var configOwner = source.Source.ConfigSetOwnerSubject ?? work.RequestedBy;
                var config = await scope.ServiceProvider.GetRequiredService<IDownloadConfigSetsRepository>()
                    .GetAsync(configOwner, source.Source.ConfigSetKey, ct)
                    ?? throw new InvalidOperationException(
                        $"Download config set '{source.Source.ConfigSetKey}' is no longer available for creator source {work.SourceId}.");
                storageKey = config.StorageKey ?? storageKey;
                cookieProfileKey = config.CookieProfileKey ?? cookieProfileKey;
                priority = config.Priority;
                configuredYtDlp = string.IsNullOrWhiteSpace(config.YtDlpOptionsJson)
                    ? null
                    : JsonSerializer.Deserialize<YtDlpOptions>(config.YtDlpOptionsJson);
            }
            await pot.EnsureAvailableAsync(ct);
            var limit = work.ScanMode == CreatorSourceScanMode.Full ? 5000 : Math.Clamp(source.Source.IncrementalPageSize, 1, 500);
            var result = await ytDlp.TryGetVideoInfoAsync(source.Source.SourceUrl, ct, flat: true,
                overrideOptions: pot.Apply((configuredYtDlp ?? new YtDlpOptions()) with
                {
                    VideoSelection = (configuredYtDlp?.VideoSelection ?? new YtDlpVideoSelectionOptions()) with
                        { PlaylistItems = $"1:{limit}" }
                }));
            if (!result.Success || result.Data is not { } container) throw new InvalidOperationException(result.ErrorOutput ?? "Creator scan failed.");
            var candidates = (container.Entries ?? []).Select(entry => Candidate(source.Source, container, entry))
                .Where(x => x is not null).Cast<DiscoveredMediaCandidate>().Take(limit).ToArray();
            var upsert = await repository.UpsertDiscoveredMediaBatchAsync(new UpsertDiscoveredMediaBatchRequestMessage
            {
                CreatorSourceId = work.SourceId, CorrelationId = Guid.NewGuid(), ScanMode = work.ScanMode,
                ScheduleKey = "lite-local", IdempotencyKey = item.DeduplicationKey, ScannedAt = clock.GetCurrentInstant(),
                ScanHighWatermarkExternalMediaId = candidates.FirstOrDefault()?.ExternalMediaId,
                ScanPageStartIndex = 1, ScanPageComplete = candidates.Length < limit, IsScanPageFinalBatch = true,
                StorageKey = storageKey, RequestedBy = work.RequestedBy,
                ConfigSetKey = source.Source.ConfigSetKey, QueueAllItems = work.QueueDiscovered,
                EncodeForPlaylist = encodeForPlaylist, Priority = priority, FetchComments = fetchComments,
                CookieSecretPath = string.IsNullOrWhiteSpace(cookieProfileKey) ? null
                    : SecretPaths.ForUserCookieProfile(work.RequestedBy, cookieProfileKey),
                YtDlpOptions = configuredYtDlp,
                SuppressDownloadEnqueue = !work.QueueDiscovered, Items = candidates
            }, ct);
            foreach (var candidate in upsert.EnqueuedItems)
            {
                await downloadIngress.AcceptAsync(new LiteDownloadSubmission
                {
                    SourceUrl = candidate.CanonicalUrl,
                    StorageKey = storageKey,
                    CookieProfileKey = cookieProfileKey,
                    Priority = priority,
                    EncodeAudioRendition = encodeForPlaylist,
                    FetchComments = fetchComments,
                    YtDlpOptions = configuredYtDlp,
                    SourceKind = DownloadSourceKind.Channel
                }, work.RequestedBy, ct);
            }
            return new WorkExecutionResult(WorkExecutionDisposition.Completed, ErrorMessage: $"Discovered {upsert.TotalSeen} item(s); queued {upsert.EnqueuedItems.Count}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Local creator scan {SourceId} failed.", work.SourceId);
            return item.Attempt < item.MaximumAttempts ? new(WorkExecutionDisposition.Retry, "creator_scan_failed", exception.Message)
                : new(WorkExecutionDisposition.Failed, "creator_scan_failed", exception.Message);
        }
    }
    Task<WorkExecutionResult> ILocalWorkExecutor<ChannelScanRefreshRequested>.ExecuteAsync(ChannelScanRefreshRequested work, CancellationToken ct) => Unsupported();
    Task<WorkExecutionResult> ILocalWorkExecutor<ChannelScanFullRequested>.ExecuteAsync(ChannelScanFullRequested work, CancellationToken ct) => Unsupported();
    Task<WorkExecutionResult> ILocalWorkExecutor<ChannelAssetRefreshRequested>.ExecuteAsync(ChannelAssetRefreshRequested work, CancellationToken ct) => Unsupported();
    private static Task<WorkExecutionResult> Unsupported() => Task.FromResult(new WorkExecutionResult(WorkExecutionDisposition.Failed, "local_payload_required"));

    private static DiscoveredMediaCandidate? Candidate(CreatorSourceEntity source, VideoInfo container, VideoInfo entry)
    {
        var id = entry.Id ?? entry.DisplayId; if (string.IsNullOrWhiteSpace(id)) return null;
        var extractor = entry.Extractor ?? entry.ExtractorKey ?? container.Extractor ?? container.ExtractorKey ?? source.Platform;
        var url = extractor.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}"
            : new[] { entry.WebpageUrl, entry.Url }.FirstOrDefault(value =>
                Uri.TryCreate(value, UriKind.Absolute, out _)
                && !SameSource(value, source.SourceUrl));
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return null;
        return new DiscoveredMediaCandidate
        {
            Platform = source.Platform, Extractor = extractor, ExternalMediaId = id, CanonicalUrl = url,
            Title = entry.Title ?? entry.FullTitle, DurationSeconds = entry.Duration,
            ThumbnailUrl = entry.Thumbnails?.Where(x => !string.IsNullOrWhiteSpace(x.Url)).OrderByDescending(x => x.Width ?? 0).FirstOrDefault()?.Url,
            LiveStatus = entry.LiveStatus?.ToString(), Availability = entry.Availability?.ToString()
        };
    }

    private static bool SameSource(string? candidate, string source)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var left)
            || !Uri.TryCreate(source, UriKind.Absolute, out var right)) return false;
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
               && left.Port == right.Port
               && string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
               && string.Equals(left.Query, right.Query, StringComparison.Ordinal);
    }
}

public sealed class LiteImportExecutionHandler(
    IServiceScopeFactory scopeFactory, IStoreProvider storeProvider, IOptions<LiteAcquisitionOptions> options,
    ILocalExecutionStore executionStore,
    ILogger<LiteImportExecutionHandler> logger) : ILocalExecutionHandler, IImportWorkExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = LiteDownloadIngress.CreateJsonOptions();
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".mp3", ".m4a", ".opus", ".flac", ".wav" };
    public string Kind => LiteAcquisitionKinds.LocalImport;
    public async Task<WorkExecutionResult> ExecuteAsync(LocalExecutionItem item, CancellationToken ct)
    {
        try
        {
            if (item.Payload.TryGetProperty("itemId", out _)) return await ImportItemAsync(item, ct);
            return await ScanAsync(item, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Local import work {WorkId} failed.", item.WorkId);
            if (item.Attempt < item.MaximumAttempts)
                return new(WorkExecutionDisposition.Retry, "local_import_failed", exception.Message);
            await SettleItemFailureAsync(item, "local_import_failed", exception.Message);
            return new(WorkExecutionDisposition.Failed, "local_import_failed", exception.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var snapshot = await executionStore.LoadSnapshotAsync(item.WorkId.ToString(), CancellationToken.None);
            if (snapshot.Items.SingleOrDefault()?.Status == LocalExecutionStatus.CancellationRequested)
            {
                await SettleItemFailureAsync(item, "cancelled", "Import cancelled.");
                return new(WorkExecutionDisposition.Cancelled, "cancelled", "Import cancelled.");
            }
            return new(WorkExecutionDisposition.Retry, "process_interrupted", "Import execution was interrupted and will be recovered.");
        }
    }
    Task<WorkExecutionResult> ILocalWorkExecutor<IFlowMessage>.ExecuteAsync(IFlowMessage work, CancellationToken ct)
        => Task.FromResult(new WorkExecutionResult(WorkExecutionDisposition.Failed, "local_payload_required"));

    private async Task<WorkExecutionResult> ScanAsync(LocalExecutionItem item, CancellationToken ct)
    {
        var work = item.Payload.Deserialize<LiteImportScanWork>(JsonOptions)!;
        var root = Path.GetFullPath(options.Value.IncomingPath);
        var scan = Path.GetFullPath(Path.Combine(root, work.SubPath ?? string.Empty));
        if (!scan.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && scan != root)
            throw new InvalidOperationException("Import path escapes the configured incoming directory.");
        var rows = Directory.EnumerateFiles(scan, "*", SearchOption.AllDirectories)
            .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path)).Select(file => new ImportSessionScannedItem
            {
                RelativePath = Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), FileName = file.Name,
                FileSizeBytes = file.Length, FileMtime = Instant.FromDateTimeUtc(file.LastWriteTimeUtc),
                MetadataState = ImportSessionItemMetadataState.Incomplete,
                MetadataSource = ImportSessionItemMetadataSource.Placeholder
            }).ToArray();
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IImportSessionRepository>().IngestScannedItemsAsync(work.SessionId, rows, ct);
        return new WorkExecutionResult(WorkExecutionDisposition.Completed, ErrorMessage: $"Scanned {rows.Length} media file(s).");
    }

    private async Task<WorkExecutionResult> ImportItemAsync(LocalExecutionItem item, CancellationToken ct)
    {
        var work = item.Payload.Deserialize<LiteImportItemWork>(JsonOptions)!;
        await using var scope = scopeFactory.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        var source = await imports.GetItemWorkAsync(work.SessionId, work.ItemId, ct) ?? throw new InvalidOperationException("Import item was not found.");
        var root = Path.GetFullPath(options.Value.IncomingPath);
        var path = Path.GetFullPath(Path.Combine(root, source.RelativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            throw new InvalidOperationException("Import source is missing or outside the configured incoming directory.");
        await imports.MarkItemHashingAsync(work.SessionId, work.ItemId, ct);
        var hash = await HashAsync(path, ct);
        var prepared = new LocalImportFilePrepared
        {
            JobId = work.ItemId, CorrelationId = source.CorrelationId, MessageId = Guid.NewGuid(),
            OperationKey = item.DeduplicationKey, OccurredAt = SystemClock.Instance.GetCurrentInstant(), Attempt = item.Attempt,
            BatchId = work.SessionId, ItemId = work.ItemId, SourceFileRef = path, FileName = source.FileName,
            FileSizeBytes = new FileInfo(path).Length, ContentHashXxh128 = hash
        };
        await imports.MarkItemPreparedAsync(work.SessionId, work.ItemId, prepared, ct);
        var reservation = await scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>().ReserveVersionAsync(new VersionReservationRequest
        {
            JobId = work.ItemId, ContentHashXxh128 = hash, StorageKey = source.StorageKey, FileName = source.FileName,
            Provider = source.Provider, SourceMediaId = source.SourceMediaId, SourceLastModified = source.SourceLastModified,
            IngestOrigin = IngestOrigin.LocalImport, LinkSourceToDownloadJob = false
        }, ct);
        await imports.MarkItemUploadingAsync(work.SessionId, work.ItemId, reservation.MediaGuid, reservation.StoragePath, ct);
        var storage = await storeProvider.GetAsync(source.StorageKey, ct);
        if (!await LiteArtifactReconciler.MatchesAsync(
                storage, reservation.StoragePath, hash, prepared.FileSizeBytes, ct))
        {
            await using var input = File.OpenRead(path); await storage.SetObject(reservation.StoragePath, input, false, ct);
        }
        await imports.MarkItemImportedAsync(work.SessionId, work.ItemId, reservation.MediaGuid, reservation.StoragePath,
            null, null, null, null, null, ct);
        await imports.CompleteSessionIfTerminalAsync(work.SessionId, ct);
        if (source.DeleteSourceFiles) File.Delete(path);
        return new WorkExecutionResult(reservation.ContentAlreadyStored ? WorkExecutionDisposition.AlreadyCompleted : WorkExecutionDisposition.Completed);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        var hasher = new XxHash128(); var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try { await using var stream = File.OpenRead(path); int read; while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0) hasher.Append(buffer.AsSpan(0, read)); return Convert.ToHexStringLower(hasher.GetCurrentHash()); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task SettleItemFailureAsync(LocalExecutionItem item, string code, string message)
    {
        if (!item.Payload.TryGetProperty("itemId", out _)) return;
        var work = item.Payload.Deserialize<LiteImportItemWork>(JsonOptions);
        if (work is null) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var imports = scope.ServiceProvider.GetRequiredService<IImportSessionRepository>();
        await imports.MarkItemCommitFailedAsync(work.SessionId, work.ItemId, code, message, ct: CancellationToken.None);
        await imports.CompleteSessionIfTerminalAsync(work.SessionId, CancellationToken.None);
    }
}
