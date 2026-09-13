using System.Buffers;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using DataBridge.Data;
using FluentStorage.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Application;
using Shared.Database;
using Shared.Metadata;
using Shared.Messaging;
using Shared.Secrets;
using Shared.Storage;
using YtDlpSharpLib;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Progress;

namespace DataBridge.Lite;

public interface ILiteDownloadIngress
{
    Task<DurableWorkReceipt> AcceptAsync(
        LiteDownloadSubmission submission,
        string ownerSubject,
        CancellationToken cancellationToken = default);
}

/// <summary>Commits the normal download job/run rows before exposing the ledger receipt.</summary>
public sealed class LiteDownloadIngress(
    IServiceScopeFactory scopeFactory,
    ILocalExecutionStore executionStore,
    ILocalExecutionWakeSignal wakeSignal,
    IClock clock) : ILiteDownloadIngress
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<DurableWorkReceipt> AcceptAsync(
        LiteDownloadSubmission submission,
        string ownerSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!Uri.TryCreate(submission.SourceUrl, UriKind.Absolute, out var source)
            || source.Scheme is not ("http" or "https"))
            throw new ArgumentException("SourceUrl must be an absolute HTTP(S) URL.", nameof(submission));
        if (submission.Priority is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(submission), "Priority must be between 0 and 100.");

        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var request = new DownloadRequested
        {
            JobId = jobId,
            CorrelationId = correlationId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"lite/download/{jobId:N}",
            OccurredAt = clock.GetCurrentInstant(),
            SourceUrl = source.AbsoluteUri,
            RequestedBy = ownerSubject,
            StorageKey = string.IsNullOrWhiteSpace(submission.StorageKey) ? "default" : submission.StorageKey.Trim(),
            Tags = submission.Tags,
            ForceDownload = submission.ForceDownload,
            SourceKind = submission.SourceKind,
            MediaKind = submission.MediaKind,
            AudioFormat = submission.AudioFormat,
            EncodeAudioRendition = submission.EncodeAudioRendition,
            FetchComments = submission.FetchComments,
            Priority = submission.Priority,
            PresetKey = submission.PresetKey,
            YtDlpOptions = submission.YtDlpOptions,
            CookieSecretPath = string.IsNullOrWhiteSpace(submission.CookieProfileKey)
                ? null
                : SecretPaths.ForUserCookieProfile(ownerSubject, submission.CookieProfileKey)
        };

        await using var scope = scopeFactory.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
            .CreateInitialRunAsync(request, autoStart: true, cancellationToken)
            ?? throw new InvalidOperationException("The local download request could not create a durable run.");
        var work = new LiteDownloadWork
        {
            JobId = jobId,
            RunId = run.RunId,
            CorrelationId = correlationId,
            SourceUrl = request.SourceUrl,
            StorageKey = request.StorageKey!,
            PresetKey = request.PresetKey,
            CookieSecretPath = request.CookieSecretPath,
            Tags = request.Tags,
            ForceDownload = request.ForceDownload,
            SourceKind = request.SourceKind,
            MediaKind = request.MediaKind,
            AudioFormat = request.AudioFormat,
            EncodeAudioRendition = request.EncodeAudioRendition,
            FetchComments = request.FetchComments,
            Priority = request.Priority,
            YtDlpOptions = request.YtDlpOptions
        };
        var item = await executionStore.EnqueueAsync(
            LiteAcquisitionKinds.Download,
            $"download:{jobId:N}:{run.RunId:N}",
            JsonSerializer.SerializeToElement(work, JsonOptions),
            maximumAttempts: 3,
            cancellationToken);
        wakeSignal.Wake();
        return new DurableWorkReceipt(DurableWorkDisposition.Accepted, item.WorkId, item.DeduplicationKey);
    }

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }
}

/// <summary>
/// Runs yt-dlp, uploads bytes and sidecars, writes catalog metadata, and settles the existing
/// download job in one local executor. Stable work directories and content/object checks make a
/// process-death retry converge without a second catalog version or upload.
/// </summary>
public sealed class LiteDownloadExecutionHandler(
    IServiceScopeFactory scopeFactory,
    IYtDlpClient ytDlp,
    IStoreProvider storeProvider,
    ISecretStore secretStore,
    ILitePotProvider potProvider,
    ILocalExecutionStore executionStore,
    ILocalProgressHub<LocalExecutionEvent, LocalExecutionSnapshot> progressHub,
    IOptions<LiteAcquisitionOptions> options,
    IClock clock,
    ILogger<LiteDownloadExecutionHandler> logger) : ILocalExecutionHandler, IDownloadWorkExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = LiteDownloadIngress.CreateJsonOptions();
    public string Kind => LiteAcquisitionKinds.Download;
    public string ConcurrencyGroup => "download";

    public async Task<WorkExecutionResult> ExecuteAsync(LocalExecutionItem item, CancellationToken cancellationToken)
    {
        LiteDownloadWork work;
        try
        {
            work = item.Payload.Deserialize<LiteDownloadWork>(JsonOptions)
                   ?? throw new JsonException("The local download payload was empty.");
        }
        catch (Exception exception)
        {
            return new WorkExecutionResult(WorkExecutionDisposition.Failed, "invalid_download_payload", exception.Message);
        }

        var workPath = Path.Combine(Path.GetFullPath(options.Value.TempPath), item.WorkId.ToString("N"));
        var cookiePath = Path.Combine(workPath, "cookies");
        Directory.CreateDirectory(workPath);
        try
        {
            await ReportAsync(item, 1, 0, "Validating storage and acquisition provider.", cancellationToken);
            var storage = await storeProvider.GetAsync(work.StorageKey, cancellationToken);
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                probe.CancelAfter(options.Value.StorageProbeTimeout);
                await storage.ListObjects(new StorageListOptions { MaxResults = 1 }, probe.Token);
            }
            await potProvider.EnsureAvailableAsync(cancellationToken);

            var ytDlpOptions = await ResolveOptionsAsync(work, cancellationToken);
            await using var cookies = await LiteCookieMaterializer.CreateAsync(
                secretStore, work.CookieSecretPath, cookiePath, logger, cancellationToken);
            ytDlpOptions = OperationalOptions(ytDlpOptions, cookies.FilePath);

            await ReportAsync(item, 2, 1, "Resolving source metadata.", cancellationToken);
            var metadataResult = await ytDlp.TryGetVideoInfoAsync(
                work.SourceUrl, cancellationToken, fetchComments: work.FetchComments, overrideOptions: ytDlpOptions);
            if (!metadataResult.Success || metadataResult.Data is not { } info)
                return await FailAsync(work, item, "source_metadata_failed",
                    metadataResult.ErrorOutput ?? "yt-dlp did not return source metadata.", cancellationToken);
            var provider = string.IsNullOrWhiteSpace(info.Extractor) ? info.ExtractorKey : info.Extractor;
            var sourceMediaId = info.Id ?? info.DisplayId;
            var metadataEvent = new MetadataFetched
            {
                JobId = work.JobId,
                CorrelationId = work.CorrelationId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"lite/download/{work.JobId:N}/metadata",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = item.Attempt,
                Provider = provider,
                SourceMediaId = sourceMediaId,
                Title = info.Title ?? info.FullTitle,
                Uploader = info.Uploader ?? info.Channel,
                MetaFile = new MetaFile { Title = info.Title ?? info.FullTitle, OriginalUrl = work.SourceUrl }
            };

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
                await jobs.ApplyMetadataAsync(work.JobId, metadataEvent, cancellationToken);
                var duplicate = await jobs.CheckSourceVersionAsync(metadataEvent, work.ForceDownload, cancellationToken);
                if (duplicate.AlreadyDownloaded && duplicate.MediaGuid is { } mediaGuid)
                {
                    await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
                        .MarkAlreadyDownloadedAsync(work.JobId, work.RunId, mediaGuid, provider, sourceMediaId,
                            ct: cancellationToken);
                    await ReportAsync(item, 3, 100, "Source is already present in the catalog.", cancellationToken);
                    Cleanup(workPath);
                    return new WorkExecutionResult(WorkExecutionDisposition.AlreadyCompleted);
                }
            }

            var checkpoint = Path.Combine(workPath, "download.complete.json");
            var mediaPath = FindMedia(workPath);
            if (mediaPath is null || !File.Exists(checkpoint))
            {
                CleanupAcquisitionFiles(workPath);
                await ReportAsync(item, 4, 2, "Downloading media.", cancellationToken);
                var reporter = new DurableProgressReporter(item, executionStore, progressHub, logger, cancellationToken);
                await DownloadAsync(work, workPath, ytDlpOptions, reporter, cancellationToken);
                await reporter.FlushAsync();
                mediaPath = FindMedia(workPath)
                            ?? throw new InvalidOperationException("yt-dlp completed without a media file.");
                var hash = await HashAsync(mediaPath, cancellationToken);
                await File.WriteAllTextAsync(checkpoint,
                    JsonSerializer.Serialize(new DownloadCheckpoint(Path.GetFileName(mediaPath), hash)), cancellationToken);
            }

            var saved = JsonSerializer.Deserialize<DownloadCheckpoint>(await File.ReadAllTextAsync(checkpoint, cancellationToken))
                        ?? throw new InvalidOperationException("The acquisition checkpoint is invalid.");
            mediaPath = Path.Combine(workPath, saved.FileName);
            if (!File.Exists(mediaPath) || !string.Equals(await HashAsync(mediaPath, cancellationToken), saved.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The acquired media no longer matches its durable checkpoint.");

            await ReportAsync(item, 1000, 92, "Reconciling artifact storage.", cancellationToken);
            var file = new FileInfo(mediaPath);
            VersionReservation reservation;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                reservation = await scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>()
                    .ReserveVersionAsync(new VersionReservationRequest
                    {
                        JobId = work.JobId,
                        ContentHashXxh128 = saved.Hash,
                        StorageKey = work.StorageKey,
                        FileName = file.Name,
                        Provider = provider,
                        SourceMediaId = sourceMediaId,
                        PersistSourceMapping = true,
                        LinkSourceToDownloadJob = true
                    }, cancellationToken);
            }

            if (!await LiteArtifactReconciler.MatchesAsync(
                    storage, reservation.StoragePath, saved.Hash, file.Length, cancellationToken))
            {
                await using var sourceStream = File.OpenRead(mediaPath);
                await storage.SetObject(reservation.StoragePath, sourceStream, append: false, cancellationToken);
            }
            var artifacts = new List<DownloadArtifactSnapshot>
            {
                StoredArtifact(work, DownloadStage.PrimaryMediaUpload, "primary", UploadArtifactKind.Primary,
                    required: true, mediaPath, reservation.StoragePath, saved.Hash, file.Length)
            };

            var infoPath = Path.Combine(workPath, "media.info.json");
            string? infoStoragePath = null;
            if (File.Exists(infoPath))
            {
                infoStoragePath = SidecarPath(reservation.StoragePath, "media.info.json");
                var infoHash = await HashAsync(infoPath, cancellationToken);
                var infoSize = new FileInfo(infoPath).Length;
                if (!await LiteArtifactReconciler.MatchesAsync(
                        storage, infoStoragePath, infoHash, infoSize, cancellationToken))
                {
                    await using var infoStream = File.OpenRead(infoPath);
                    await storage.SetObject(infoStoragePath, infoStream, append: false, cancellationToken);
                }
                artifacts.Add(StoredArtifact(work, DownloadStage.InfoJsonUpload, "info-json", UploadArtifactKind.InfoJson,
                    required: true, infoPath, infoStoragePath, infoHash, infoSize));
            }

            string? thumbnailStoragePath = null;
            var captions = new List<CapturedCaptionMetadata>();
            foreach (var sidecar in Directory.EnumerateFiles(workPath, "media.*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(sidecar);
                var isThumbnail = ThumbnailExtensions.Contains(extension);
                var isCaption = SubtitleExtensions.Contains(extension);
                if (!isThumbnail && !isCaption) continue;
                var storagePath = SidecarPath(reservation.StoragePath, Path.GetFileName(sidecar));
                var sidecarHash = await HashAsync(sidecar, cancellationToken);
                var sidecarSize = new FileInfo(sidecar).Length;
                if (!await LiteArtifactReconciler.MatchesAsync(
                        storage, storagePath, sidecarHash, sidecarSize, cancellationToken))
                {
                    await using var input = File.OpenRead(sidecar);
                    await storage.SetObject(storagePath, input, append: false, cancellationToken);
                }
                if (isThumbnail && thumbnailStoragePath is null)
                {
                    thumbnailStoragePath = storagePath;
                    artifacts.Add(StoredArtifact(work, DownloadStage.ThumbnailUpload, "thumbnail",
                        UploadArtifactKind.Thumbnail, required: false, sidecar, storagePath, sidecarHash, sidecarSize));
                }
                if (isCaption)
                {
                    var language = CaptionLanguage(Path.GetFileName(sidecar));
                    artifacts.Add(StoredArtifact(work, DownloadStage.CaptionUpload,
                        $"caption:{captions.Count}:{language}", UploadArtifactKind.Caption, required: false,
                        sidecar, storagePath, sidecarHash, sidecarSize));
                    captions.Add(new CapturedCaptionMetadata
                    {
                        StoragePath = storagePath,
                        CaptionType = "subtitles",
                        LanguageCode = language
                    });
                }
            }

            var metaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                mediaGuid = reservation.MediaGuid.ToString("D"),
                title = info.Title ?? info.FullTitle,
                contentHashXxh128 = saved.Hash,
                originalUrl = work.SourceUrl
            }));
            var metaHash = Convert.ToHexStringLower(XxHash128.Hash(metaBytes));
            var metaStoragePath = SidecarPath(reservation.StoragePath, $"{reservation.MediaGuid:N}.meta");
            if (!await LiteArtifactReconciler.MatchesAsync(
                    storage, metaStoragePath, metaHash, metaBytes.Length, cancellationToken))
            {
                await using var metaStream = new MemoryStream(metaBytes, writable: false);
                await storage.SetObject(metaStoragePath, metaStream, append: false, cancellationToken);
            }
            artifacts.Add(StoredArtifact(work, DownloadStage.MetaSidecarUpload, "meta", UploadArtifactKind.Meta,
                required: true, null, metaStoragePath, metaHash, metaBytes.Length));

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
                var flow = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
                await jobs.ApplyDownloadCompletedAsync(work.JobId, new DownloadCompleted
                {
                    JobId = work.JobId,
                    CorrelationId = work.CorrelationId,
                    MessageId = Guid.NewGuid(),
                    OperationKey = $"lite/download/{work.JobId:N}/acquired",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = item.Attempt,
                    TempFileRef = mediaPath,
                    FileName = file.Name,
                    FileSizeBytes = file.Length,
                    ContentHashXxh128 = saved.Hash
                }, cancellationToken);
                await jobs.CommitUploadAsync(work.JobId, new UploadCompleted
                {
                    JobId = work.JobId,
                    CorrelationId = work.CorrelationId,
                    MessageId = Guid.NewGuid(),
                    OperationKey = $"lite/download/{work.JobId:N}/stored",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = item.Attempt,
                    StorageKey = work.StorageKey,
                    StoragePath = reservation.StoragePath,
                    ContentHashXxh128 = saved.Hash,
                    ContentLengthBytes = file.Length,
                    Kind = UploadArtifactKind.Primary
                }, cancellationToken);
                if (infoStoragePath is not null)
                {
                    await jobs.ApplySidecarUploadCompletedAsync(work.JobId, new UploadCompleted
                    {
                        JobId = work.JobId,
                        CorrelationId = work.CorrelationId,
                        MessageId = Guid.NewGuid(),
                        OperationKey = $"lite/download/{work.JobId:N}/info-json",
                        OccurredAt = clock.GetCurrentInstant(),
                        Attempt = item.Attempt,
                        StorageKey = work.StorageKey,
                        StoragePath = infoStoragePath,
                        ContentHashXxh128 = await HashAsync(infoPath, cancellationToken),
                        ContentLengthBytes = new FileInfo(infoPath).Length,
                        Kind = UploadArtifactKind.InfoJson
                    }, cancellationToken);
                    var mapped = YtDlpMetadataMapper.Map(info, provider ?? string.Empty, clock);
                    mapped = mapped with
                    {
                        Media = mapped.Media with { ThumbnailStoragePath = thumbnailStoragePath },
                        Captions = captions,
                        Tags = mapped.Tags.Concat(work.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                    await scope.ServiceProvider.GetRequiredService<IMetadataRepository>()
                        .WriteMetadataAsync(reservation.MediaGuid, mapped, work.StorageKey, cancellationToken);
                }
                await jobs.ApplyMetaUploadCompletedAsync(work.JobId, new UploadCompleted
                {
                    JobId = work.JobId,
                    CorrelationId = work.CorrelationId,
                    MessageId = Guid.NewGuid(),
                    OperationKey = $"lite/download/{work.JobId:N}/meta",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = item.Attempt,
                    StorageKey = work.StorageKey,
                    StoragePath = metaStoragePath,
                    ContentHashXxh128 = metaHash,
                    ContentLengthBytes = metaBytes.Length,
                    Kind = UploadArtifactKind.Meta
                }, cancellationToken);
                foreach (var artifact in artifacts)
                    await flow.UpsertArtifactAsync(artifact, cancellationToken);
                if (work.SourceKind == DownloadSourceKind.Playlist)
                    await scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>()
                        .TryLinkMediaGuidAsync(work.JobId, reservation.MediaGuid, cancellationToken);
                var completed = await flow.CompleteRunAsync(
                    work.JobId, work.RunId, withWarnings: infoStoragePath is null, cancellationToken);
                if (!completed)
                    throw new InvalidOperationException("The catalog commit was rejected because the download run is no longer current.");
            }

            await ReportAsync(item, 2000, 100, "Download stored and committed to the catalog.", cancellationToken);
            Cleanup(workPath);
            return new WorkExecutionResult(WorkExecutionDisposition.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The dispatcher uses the same token for an explicit user cancellation and for
            // ownership/host loss. Only the durable cancellation_requested state authorizes
            // settling the normal download run as stopped; interrupted owners leave it active
            // so startup recovery can resume from the stable work directory/checkpoint.
            var snapshot = await executionStore.LoadSnapshotAsync(item.WorkId.ToString(), CancellationToken.None);
            if (snapshot.Items.SingleOrDefault()?.Status == LocalExecutionStatus.CancellationRequested)
            {
                await MarkStoppedAsync(work);
                return new WorkExecutionResult(WorkExecutionDisposition.Cancelled, "cancelled", "Download cancelled.");
            }
            return new WorkExecutionResult(WorkExecutionDisposition.Retry, "process_interrupted",
                "Download execution was interrupted and will be recovered.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Lite acquisition failed for job {JobId} on attempt {Attempt}.", work.JobId, item.Attempt);
            if (item.Attempt < item.MaximumAttempts)
                return new WorkExecutionResult(WorkExecutionDisposition.Retry, "acquisition_failed",
                    "Acquisition failed and will be retried.", TimeSpan.FromSeconds(5));
            return await FailAsync(work, item, "acquisition_failed", exception.Message, CancellationToken.None);
        }
    }

    Task<WorkExecutionResult> ILocalWorkExecutor<IFlowMessage>.ExecuteAsync(IFlowMessage work, CancellationToken cancellationToken)
        => Task.FromResult(new WorkExecutionResult(WorkExecutionDisposition.Failed, "local_payload_required",
            "Lite download execution requires its persisted local work envelope."));

    private async Task<YtDlpOptions> ResolveOptionsAsync(LiteDownloadWork work, CancellationToken cancellationToken)
    {
        if (work.YtDlpOptions is not null || string.IsNullOrWhiteSpace(work.PresetKey))
            return work.YtDlpOptions ?? new YtDlpOptions();
        await using var scope = scopeFactory.CreateAsyncScope();
        var preset = await scope.ServiceProvider.GetRequiredService<IOptionPresetsRepository>()
            .GetByKeyAsync(work.PresetKey, cancellationToken);
        return preset is null ? new YtDlpOptions() : JsonSerializer.Deserialize<YtDlpOptions>(preset.YtDlpOptionsJson) ?? new YtDlpOptions();
    }

    private YtDlpOptions OperationalOptions(YtDlpOptions source, string? cookieFile)
    {
        var safe = source with
        {
            AdvancedArguments = [],
            VideoSelection = source.VideoSelection with { NoPlaylist = true, DownloadArchive = null },
            Filesystem = source.Filesystem with
            {
                Paths = null, Output = null, BatchFile = null, LoadInfoJson = null, CacheDir = null,
                NoPart = true, WriteInfoJson = true,
                Cookies = cookieFile, NoCookies = cookieFile is null && source.Filesystem.NoCookies,
                CookiesFromBrowser = cookieFile is null ? source.Filesystem.CookiesFromBrowser : null
            },
            PostProcessing = source.PostProcessing with
            {
                FfmpegLocation = options.Value.FfmpegPath,
                Exec = [], NoExec = false, UsePostprocessor = [], PostprocessorArgs = []
            },
            General = source.General with
            {
                ConfigLocations = [], PluginDirs = [], Update = false, UpdateTo = null,
                JsRuntimes = [], RemoteComponents = [], Alias = [], PresetAlias = []
            },
            VerbositySimulation = source.VerbositySimulation with { Newline = true },
            Thumbnail = source.Thumbnail with { WriteThumbnail = !source.Thumbnail.NoWriteThumbnail }
        };
        return potProvider.Apply(safe);
    }

    private Task DownloadAsync(LiteDownloadWork work, string path, YtDlpOptions ytOptions,
        IProgress<YtDlpProgress> progress, CancellationToken cancellationToken)
        => work.MediaKind == MediaKind.Audio
            ? ytDlp.DownloadAudioAsync(work.SourceUrl, path, new AudioDownloadOptions
            {
                AbortOnError = true, OutputTemplate = "media.%(ext)s", OverwriteFiles = true,
                RestrictFilenames = true, AudioFormat = work.AudioFormat ?? AudioConversionFormat.M4a, YtDlp = ytOptions
            }, progress, cancellationToken)
            : ytDlp.DownloadAsync(work.SourceUrl, path, new DownloadOptions
            {
                AbortOnError = true, OutputTemplate = "media.%(ext)s", OverwriteFiles = true,
                RestrictFilenames = true, YtDlp = ytOptions
            }, progress, cancellationToken);

    private async Task<WorkExecutionResult> FailAsync(LiteDownloadWork work, LocalExecutionItem item,
        string code, string message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>()
            .FailRunAsync(work.JobId, work.RunId, FailureKind.Permanent, code, message, cancellationToken);
        return new WorkExecutionResult(WorkExecutionDisposition.Failed, code, message);
    }

    private async Task MarkStoppedAsync(LiteDownloadWork work)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDownloadFlowV2Repository>();
            await repository.RequestStopAsync(work.JobId, requestedBy: null, reason: "Lite acquisition cancelled.");
            await repository.MarkStoppedAsync(work.JobId, work.RunId, "Lite acquisition cancelled.");
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not settle cancelled job {JobId}.", work.JobId); }
    }

    private async Task ReportAsync(LocalExecutionItem item, int sequence, double? percent, string message, CancellationToken ct)
    {
        if (!await executionStore.ReportProgressAsync(item.WorkId, sequence, percent, message, ct)) return;
        var evt = new LocalExecutionEvent(item.WorkId, item.Kind, LocalExecutionStatus.Running, item.Attempt,
            DateTimeOffset.UtcNow, ProgressSequence: sequence, ProgressPercent: percent, ProgressMessage: message);
        progressHub.Publish("all", evt);
        progressHub.Publish(item.WorkId.ToString(), evt);
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>()
            .AppendProgressLogAsync(item.Payload.Deserialize<LiteDownloadWork>(JsonOptions)!.JobId, sequence, message, ct);
    }

    private static string? FindMedia(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "media.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(file => !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                    && !ThumbnailExtensions.Contains(Path.GetExtension(file))
                                    && !SubtitleExtensions.Contains(Path.GetExtension(file))
                                    && Path.GetExtension(file) is not (".part" or ".ytdl" or ".temp"))
        : null;

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                hasher.Append(buffer.AsSpan(0, read));
            return Convert.ToHexStringLower(hasher.GetCurrentHash());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string SidecarPath(string primaryPath, string name)
        => $"{primaryPath[..(primaryPath.LastIndexOf('/') + 1)]}{name}";
    private static DownloadArtifactSnapshot StoredArtifact(
        LiteDownloadWork work, DownloadStage stage, string key, UploadArtifactKind kind, bool required,
        string? tempFileRef, string storagePath, string hash, long size)
        => new()
        {
            JobId = work.JobId,
            RunId = work.RunId,
            Stage = stage,
            ArtifactKey = key,
            Kind = kind,
            Required = required,
            Status = DownloadArtifactStatus.Stored,
            TempFileRef = tempFileRef,
            StorageKey = work.StorageKey,
            StoragePath = storagePath,
            ContentHashXxh128 = hash,
            SizeBytes = size
        };
    private static string CaptionLanguage(string fileName)
    {
        var value = fileName.StartsWith("media.", StringComparison.OrdinalIgnoreCase) ? fileName[6..] : fileName;
        var dot = value.LastIndexOf('.');
        return dot > 0 ? value[..dot] : "und";
    }
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".vtt", ".srt", ".ass", ".ssa", ".lrc", ".ttml", ".sbv", ".dfxp", ".json3", ".srv1", ".srv2", ".srv3", ".scc" };
    private static void Cleanup(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    private static void CleanupAcquisitionFiles(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path))
            if (!Path.GetFileName(file).StartsWith("cookies-", StringComparison.Ordinal)) File.Delete(file);
    }

    private sealed record DownloadCheckpoint(string FileName, string Hash);

    private sealed class DurableProgressReporter(
        LocalExecutionItem item,
        ILocalExecutionStore store,
        ILocalProgressHub<LocalExecutionEvent, LocalExecutionSnapshot> hub,
        ILogger logger,
        CancellationToken cancellationToken) : IProgress<YtDlpProgress>
    {
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;
        private int _sequence = 10;
        public void Report(YtDlpProgress value)
        {
            // Reserve 1000+ for deterministic reconciliation/finalization milestones.
            var sequence = Math.Min(Interlocked.Increment(ref _sequence), 999);
            var message = value.RawLine ?? value.Message ?? value.AdditionalInfo ?? value.Phase.ToString();
            lock (_gate) _tail = _tail.ContinueWith(async _ =>
            {
                try
                {
                    if (!await store.ReportProgressAsync(item.WorkId, sequence, value.Percent, message, cancellationToken)) return;
                    var evt = new LocalExecutionEvent(item.WorkId, item.Kind, LocalExecutionStatus.Running, item.Attempt,
                        DateTimeOffset.UtcNow, ProgressSequence: sequence, ProgressPercent: value.Percent, ProgressMessage: message);
                    hub.Publish("all", evt); hub.Publish(item.WorkId.ToString(), evt);
                }
                catch (Exception exception) { logger.LogDebug(exception, "Could not persist advisory acquisition progress."); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
        public Task FlushAsync() { lock (_gate) return _tail; }
    }
}
