using System.Text;
using System.Text.Json;
using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using Conduit.NATS;
using DataBridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Database;
using Shared.Imports;
using Shared.Messaging;
using Shared.Metadata;
using Shared.Storage;
using YtDlpSharpLib.Models;

namespace DataBridge.Flows;

/// <summary>
/// Imports one approved session item. The flow suspends while Worker commands are in flight and
/// replays <see cref="Run"/> from the top on every resume, so every side effect (repo write,
/// publish, reservation) must be wrapped in an effect and every command MessageId must be stable
/// across replays; results are matched to their dispatch via CausationId.
/// </summary>
[GenerateFlows]
public class LocalImportItemFlow(
    IJetStreamPublisher bus,
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<LocalImportItemFlow> logger) : Flow<ImportSessionItemImportRequested>
{
    private const int MaxAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static bool IsControlFlowException(Exception ex)
        => ex is Cleipnir.ResilientFunctions.Domain.Exceptions.Commands.SuspendInvocationException
            or Cleipnir.ResilientFunctions.Domain.Exceptions.Commands.PostponeInvocationException;

    public override async Task Run(ImportSessionItemImportRequested request)
    {
        var work = await Capture(() => ImportRepoCall(r => r.GetItemWorkAsync(request.SessionId, request.ItemId)));
        if (work is null)
            return;

        await Capture(() => ImportRepoCall(r => r.MarkItemHashingAsync(request.SessionId, request.ItemId)));

        var sidecars = ParseSidecars(work.SidecarsJson);
        var prepared = await RunPrepareStepAsync(request, work, sidecars);
        if (prepared is null)
            return;

        await Capture(() => ImportRepoCall(r => r.MarkItemPreparedAsync(request.SessionId, request.ItemId, prepared)));

        VersionReservation reservation;
        try
        {
            reservation = await Capture(() => RepoCall(r => r.ReserveVersionAsync(new VersionReservationRequest
            {
                JobId = request.ItemId,
                ContentHashXxh128 = prepared.ContentHashXxh128,
                StorageKey = work.StorageKey,
                FileName = prepared.FileName,
                Provider = work.Provider,
                SourceMediaId = work.SourceMediaId,
                SourceLastModified = work.SourceLastModified,
                IngestOrigin = IngestOrigin.LocalImport,
                LinkSourceToDownloadJob = false
            })));
        }
        catch (Exception ex) when (!IsControlFlowException(ex))
        {
            await Capture(() => ImportRepoCall(r => r.MarkItemCommitFailedAsync(request.SessionId, request.ItemId, "reservation_failed", ex.Message)));
            await Capture(() => ImportRepoCall(r => r.CompleteSessionIfTerminalAsync(request.SessionId)));
            return;
        }

        if (reservation.ContentAlreadyStored)
        {
            await Capture(() => ImportRepoCall(r => r.MarkItemAlreadyImportedAsync(
                request.SessionId,
                request.ItemId,
                reservation.MediaGuid,
                reservation.StoragePath,
                prepared)));
            await Capture(() => ImportRepoCall(r => r.CompleteSessionIfTerminalAsync(request.SessionId)));
            return;
        }

        await Capture(() => ImportRepoCall(r => r.MarkItemUploadingAsync(request.SessionId, request.ItemId, reservation.MediaGuid, reservation.StoragePath)));

        var uploadedObjects = new List<UploadCompleted>();
        try
        {
            var primary = await RunUploadStepAsync(
                request,
                work,
                causationId: prepared.MessageId,
                tempFileRef: prepared.SourceFileRef,
                inlineContent: null,
                work.StorageKey,
                reservation.StoragePath,
                prepared.ContentHashXxh128,
                UploadArtifactKind.Primary,
                "primary");
            if (primary is null)
                throw new LocalImportItemFailedException("upload_failed", "Primary media upload failed.");
            uploadedObjects.Add(primary);

            var meta = await RunMetaUploadStepAsync(request, work, primary, reservation.MediaGuid, prepared.ContentHashXxh128);
            if (meta is null)
                throw new LocalImportItemFailedException("meta_upload_failed", ".meta sidecar upload failed.");
            uploadedObjects.Add(meta);

            string? infoJsonPath = null;
            if (prepared.InfoJson is { } infoJson)
            {
                var uploaded = await RunPreparedSidecarUploadStepAsync(request, work, primary, infoJson, UploadArtifactKind.InfoJson, "info-json");
                if (uploaded is null)
                    throw new LocalImportItemFailedException("sidecar_upload_failed", "infoJson sidecar upload failed.");
                infoJsonPath = uploaded.StoragePath;
                uploadedObjects.Add(uploaded);
            }

            string? thumbnailPath = null;
            if (prepared.Thumbnail is { } thumbnail)
            {
                var uploaded = await RunPreparedSidecarUploadStepAsync(request, work, primary, thumbnail, UploadArtifactKind.Thumbnail, "thumbnail");
                if (uploaded is null)
                    throw new LocalImportItemFailedException("sidecar_upload_failed", "thumbnail sidecar upload failed.");
                thumbnailPath = uploaded.StoragePath;
                uploadedObjects.Add(uploaded);
            }

            var captionRows = new List<LocalImportCaptionStoragePath>();
            for (var i = 0; i < prepared.Captions.Count; i++)
            {
                var caption = prepared.Captions[i];
                var uploaded = await RunPreparedSidecarUploadStepAsync(
                    request,
                    work,
                    primary,
                    caption,
                    UploadArtifactKind.Caption,
                    $"caption-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                if (uploaded is null)
                    throw new LocalImportItemFailedException("sidecar_upload_failed", "caption sidecar upload failed.");

                uploadedObjects.Add(uploaded);
                captionRows.Add(new LocalImportCaptionStoragePath(uploaded.StoragePath, caption.LanguageCode, caption.CaptionType, caption.Name));
            }

            await Capture(() => ImportRepoCall(r => r.MarkItemFinalizingAsync(request.SessionId, request.ItemId)));

            await RunMetadataWriteStepAsync(work, reservation, infoJsonPath, thumbnailPath, captionRows);

            var captionStoragePathsJson = captionRows.Count == 0 ? null : JsonSerializer.Serialize(captionRows, JsonOptions);
            await Capture(() => ImportRepoCall(r => r.MarkItemImportedAsync(
                request.SessionId,
                request.ItemId,
                reservation.MediaGuid,
                primary.StoragePath,
                primary.StorageVersion,
                meta.StoragePath,
                infoJsonPath,
                thumbnailPath,
                captionStoragePathsJson)));
        }
        catch (LocalImportItemFailedException ex)
        {
            await CompensateAsync(request, work, reservation, uploadedObjects);
            await Capture(() => ImportRepoCall(r => r.MarkItemCommitFailedAsync(request.SessionId, request.ItemId, ex.ErrorCode, ex.Message, reservation.MediaGuid, reservation.StoragePath)));
        }
        catch (Exception ex) when (!IsControlFlowException(ex))
        {
            await CompensateAsync(request, work, reservation, uploadedObjects);
            await Capture(() => ImportRepoCall(r => r.MarkItemCommitFailedAsync(request.SessionId, request.ItemId, "item_failed", ex.Message, reservation.MediaGuid, reservation.StoragePath)));
        }

        // Deliberately not a finally: a finally runs while a suspension exception unwinds and any
        // effect captured there steals the next implicit effect id, corrupting replay alignment.
        await Capture(() => ImportRepoCall(r => r.CompleteSessionIfTerminalAsync(request.SessionId)));
    }

    private async Task<LocalImportFilePrepared?> RunPrepareStepAsync(
        ImportSessionItemImportRequested request,
        ImportSessionItemWork work,
        LocalMediaImportManifestSidecars? sidecars)
    {
        var msgId = await Capture(Guid.NewGuid);
        var cmd = new PrepareLocalImportFileCommand
        {
            JobId = request.ItemId,
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            MessageId = msgId,
            OperationKey = LocalImportFlowInstance.OperationKey(request.ItemId, request.Attempt, "prepare"),
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = request.Attempt,
            BatchId = request.SessionId,
            ItemId = request.ItemId,
            File = work.RelativePath,
            Sidecars = sidecars,
            RequiredWorkerTag = work.WorkerTag
        };
        var subject = string.IsNullOrWhiteSpace(work.WorkerTag)
            ? LocalImportSubjects.PrepareLocalImportFileCommand
            : LocalImportSubjects.PrepareLocalImportFileCommandForTag(work.WorkerTag);

        await Capture(() => Publish(subject, cmd));
        var result = await Messages.OfTypes<LocalImportFilePrepared, LocalImportFilePrepareFailed>()
            .Where(x => (x.HasFirst ? x.First!.CausationId : x.Second!.CausationId) == msgId)
            .First();
        if (result.HasFirst)
            return result.First;

        await Capture(() => ImportRepoCall(r => r.MarkItemCommitFailedAsync(
            request.SessionId,
            request.ItemId,
            result.Second.ErrorCode,
            result.Second.ErrorMessage)));
        await Capture(() => ImportRepoCall(r => r.CompleteSessionIfTerminalAsync(request.SessionId)));
        return null;
    }

    private async Task<UploadCompleted?> RunUploadStepAsync(
        ImportSessionItemImportRequested request,
        ImportSessionItemWork work,
        Guid causationId,
        string? tempFileRef,
        byte[]? inlineContent,
        string storageKey,
        string storagePath,
        string contentHash,
        UploadArtifactKind kind,
        string suffix)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var msgId = await Capture(Guid.NewGuid);
            var cmd = new UploadObjectCommand
            {
                JobId = request.ItemId,
                CorrelationId = request.CorrelationId,
                CausationId = causationId,
                MessageId = msgId,
                OperationKey = LocalImportFlowInstance.OperationKey(
                    request.ItemId,
                    request.Attempt,
                    $"upload/{suffix}/attempt/{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                TempFileRef = tempFileRef,
                InlineContent = inlineContent,
                RequiredWorkerTag = work.WorkerTag,
                StorageKey = storageKey,
                StoragePath = storagePath,
                ContentHashXxh128 = contentHash,
                Kind = kind,
                VerifyHashWhileStreaming = true
            };
            var subject = string.IsNullOrWhiteSpace(work.WorkerTag)
                ? ArtifactStorageSubjects.UploadObjectCommand
                : ArtifactStorageSubjects.UploadObjectCommandForTag(work.WorkerTag);

            await Capture(() => Publish(subject, cmd));
            var result = await Messages.OfTypes<UploadCompleted, UploadFailed>()
                .Where(x => (x.HasFirst ? x.First!.CausationId : x.Second!.CausationId) == msgId)
                .First();
            if (result.HasFirst)
                return result.First;
            if (result.Second.FailureKind is FailureKind.Permanent or FailureKind.Cancelled || attempt >= MaxAttempts)
                return null;
        }

        return null;
    }

    private async Task<UploadCompleted?> RunPreparedSidecarUploadStepAsync(
        ImportSessionItemImportRequested request,
        ImportSessionItemWork work,
        UploadCompleted primary,
        LocalImportPreparedSidecar sidecar,
        UploadArtifactKind kind,
        string suffix)
    {
        var storagePath = BuildSidecarStoragePath(primary.StoragePath, sidecar.FileName);
        return await RunUploadStepAsync(request, work, primary.MessageId, sidecar.SourceFileRef, null, work.StorageKey, storagePath, sidecar.ContentHashXxh128, kind, suffix);
    }

    private async Task<UploadCompleted?> RunMetaUploadStepAsync(
        ImportSessionItemImportRequested request,
        ImportSessionItemWork work,
        UploadCompleted primary,
        Guid mediaGuid,
        string contentHashXxh128)
    {
        var metaStoragePath = BuildSidecarStoragePath(primary.StoragePath, $"{mediaGuid:N}.meta");
        var metaContent = new
        {
            mediaGuid = mediaGuid.ToString("D"),
            title = work.Title,
            contentHashXxh128,
            sourceUrl = work.SourceUrl,
            ingestOrigin = "local_import",
            sessionId = request.SessionId.ToString("D"),
            itemId = request.ItemId.ToString("D"),
            relativePath = work.RelativePath
        };
        var metaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metaContent, JsonOptions));
        var metaHash = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(metaBytes));
        return await RunUploadStepAsync(request, work, primary.MessageId, null, metaBytes, work.StorageKey, metaStoragePath, metaHash, UploadArtifactKind.Meta, "meta");
    }

    private async Task CompensateAsync(
        ImportSessionItemImportRequested request,
        ImportSessionItemWork work,
        VersionReservation reservation,
        IReadOnlyList<UploadCompleted> uploadedObjects)
    {
        for (var i = uploadedObjects.Count - 1; i >= 0; i--)
        {
            var msgId = await DispatchUploadedObjectDeletionAsync(request, work, uploadedObjects[i], $"compensate/{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            var result = await Messages.OfTypes<UploadedObjectDeleted, UploadedObjectDeleteFailed>()
                .Where(x => (x.HasFirst ? x.First!.CausationId : x.Second!.CausationId) == msgId)
                .First();
            if (!result.HasFirst)
                logger.LogWarning("Local import item compensation delete failed for ItemId {ItemId}: {Error}", request.ItemId, result.Second.ErrorMessage);
        }

        await Capture(() => RepoCall(r => r.DeleteReservedVersionAsync(reservation.MediaGuid, reservation.VersionNum)));
        if (reservation.IsNewMediaGuid)
            await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(reservation.MediaGuid, work.Provider, work.SourceMediaId)));
    }

    private async Task<Guid> DispatchUploadedObjectDeletionAsync(
        ImportSessionItemImportRequested request,
        ImportSessionItemWork work,
        UploadCompleted uploaded,
        string operationSuffix)
    {
        var msgId = await Capture(Guid.NewGuid);
        var deletion = new DeleteUploadedObjectCommand
        {
            JobId = request.ItemId,
            CorrelationId = request.CorrelationId,
            CausationId = uploaded.MessageId,
            MessageId = msgId,
            OperationKey = LocalImportFlowInstance.OperationKey(request.ItemId, request.Attempt, $"{operationSuffix}/delete-uploaded"),
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            RequiredWorkerTag = work.WorkerTag,
            StorageKey = uploaded.StorageKey,
            StoragePath = uploaded.StoragePath,
            StorageVersion = uploaded.StorageVersion
        };
        var subject = string.IsNullOrWhiteSpace(work.WorkerTag)
            ? ArtifactStorageSubjects.DeleteUploadedObjectCommand
            : ArtifactStorageSubjects.DeleteUploadedObjectCommandForTag(work.WorkerTag);
        await Capture(() => Publish(subject, deletion));
        return msgId;
    }

    private async Task RunMetadataWriteStepAsync(
        ImportSessionItemWork work,
        VersionReservation reservation,
        string? infoJsonPath,
        string? thumbnailPath,
        IReadOnlyList<LocalImportCaptionStoragePath> captionRows)
    {
        try
        {
            await Capture(() => WriteItemMetadataAsync(work, reservation, infoJsonPath, thumbnailPath, captionRows));
            await Capture(() => messageBus.PublishAsync(MetadataSyncSubjects.SyncUpsert, new MetadataSyncUpsertMessage { MediaGuid = reservation.MediaGuid }));
        }
        catch (Exception ex) when (!IsControlFlowException(ex))
        {
            if (reservation.IsNewMediaGuid)
                await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(reservation.MediaGuid, work.Provider, work.SourceMediaId)));
            throw new LocalImportItemFailedException("metadata_write_failed", ex.Message, ex);
        }
    }

    private async Task WriteItemMetadataAsync(
        ImportSessionItemWork work,
        VersionReservation reservation,
        string? infoJsonPath,
        string? thumbnailPath,
        IReadOnlyList<LocalImportCaptionStoragePath> captionRows)
    {
        var metadata = infoJsonPath is null
            ? null
            : await TryBuildRichMetadataAsync(work, infoJsonPath, thumbnailPath, captionRows);
        metadata ??= BuildMetadata(work, reservation.MediaGuid, thumbnailPath, captionRows);
        await MetaRepoCall(r => r.WriteMetadataAsync(reservation.MediaGuid, metadata, work.StorageKey));
    }

    private async Task<CapturedMediaMetadata?> TryBuildRichMetadataAsync(
        ImportSessionItemWork work,
        string infoJsonPath,
        string? thumbnailPath,
        IReadOnlyList<LocalImportCaptionStoragePath> captionRows)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var storage = await scope.ServiceProvider
                .GetRequiredService<IBlobStorageProvider>()
                .GetAsync(work.StorageKey);
            await using var stream = await storage.OpenReadAsync(infoJsonPath)
                ?? throw new InvalidOperationException($"Info JSON was not found at {infoJsonPath}.");
            // yt-dlp writes snake_case keys; VideoInfo only annotates multi-word names and relies on
            // the source-gen context's naming policy for the rest, so plain Deserialize<VideoInfo>
            // would silently leave title/uploader/comments/… null.
            var info = await JsonSerializer.DeserializeAsync(stream, YtDlpJsonContext.Default.VideoInfo)
                ?? throw new InvalidOperationException("Info JSON was empty or invalid.");
            var provider = FirstNonBlank(info.Extractor, info.ExtractorKey, work.Provider) ?? "";
            var mapped = YtDlpMetadataMapper.Map(info, provider, clock);
            // Wizard review edits win over the sidecar's values.
            return mapped with
            {
                Media = mapped.Media with
                {
                    Title = FirstNonBlank(ReadJsonString(work.UserMetadataJson, "title"), mapped.Media.Title, work.Title),
                    WebpageUrl = FirstNonBlank(ReadJsonString(work.UserMetadataJson, "sourceUrl"), mapped.Media.WebpageUrl, work.SourceUrl),
                    ExternalMediaId = FirstNonBlank(ReadJsonString(work.UserMetadataJson, "sourceMediaId"), mapped.Media.ExternalMediaId, work.SourceMediaId),
                    DurationSeconds = mapped.Media.DurationSeconds ?? ReadProbeDuration(work.ProbeMetadataJson),
                    ThumbnailStoragePath = thumbnailPath
                },
                Captions = MapCaptions(captionRows)
            };
        }
        catch (Exception ex) when (!IsControlFlowException(ex))
        {
            logger.LogWarning(
                ex,
                "Deriving rich metadata from info.json {InfoJsonPath} failed; falling back to scan-derived metadata.",
                infoJsonPath);
            return null;
        }
    }

    private CapturedMediaMetadata BuildMetadata(
        ImportSessionItemWork work,
        Guid mediaGuid,
        string? thumbnailStoragePath,
        IReadOnlyList<LocalImportCaptionStoragePath> captionRows)
    {
        // Layered merge, highest precedence first: user edits ⊕ yt-dlp enrichment ⊕ scan-derived
        // columns (filename/info.json/NFO) ⊕ placeholder defaults.
        var title = FirstNonBlank(
            ReadJsonString(work.UserMetadataJson, "title"),
            ReadJsonString(work.EnrichedMetadataJson, "title"),
            work.Title,
            Path.GetFileNameWithoutExtension(work.FileName));
        var provider = FirstNonBlank(
            ReadJsonString(work.UserMetadataJson, "provider"),
            ReadJsonString(work.EnrichedMetadataJson, "provider"),
            ReadJsonString(work.EnrichedMetadataJson, "extractor"),
            ReadJsonString(work.EnrichedMetadataJson, "extractor_key"),
            work.Provider,
            "local")!;
        var sourceId = FirstNonBlank(
            ReadJsonString(work.UserMetadataJson, "sourceMediaId"),
            ReadJsonString(work.EnrichedMetadataJson, "sourceMediaId"),
            ReadJsonString(work.EnrichedMetadataJson, "id"),
            ReadJsonString(work.EnrichedMetadataJson, "display_id"),
            work.SourceMediaId,
            mediaGuid.ToString("N"));
        var sourceUrl = FirstNonBlank(
            ReadJsonString(work.UserMetadataJson, "sourceUrl"),
            ReadJsonString(work.EnrichedMetadataJson, "sourceUrl"),
            ReadJsonString(work.EnrichedMetadataJson, "webpage_url"),
            ReadJsonString(work.EnrichedMetadataJson, "original_url"),
            work.SourceUrl);
        var durationSeconds = ReadProbeDuration(work.ProbeMetadataJson);
        var durationTicks = durationSeconds is { } seconds ? (long)(seconds * TimeSpan.TicksPerSecond) : 0L;
        var (width, height, codec) = ReadProbeVideo(work.ProbeMetadataJson);
        var topFolder = work.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var accountName = FirstNonBlank(
            ReadJsonString(work.EnrichedMetadataJson, "channel"),
            ReadJsonString(work.EnrichedMetadataJson, "uploader"),
            topFolder,
            "Unknown")!;
        var accountHandle = FirstNonBlank(
            ReadJsonString(work.EnrichedMetadataJson, "channelId"),
            ReadJsonString(work.EnrichedMetadataJson, "channel_id"),
            ReadJsonString(work.EnrichedMetadataJson, "uploader_id"),
            topFolder,
            "unknown")!;

        return new CapturedMediaMetadata
        {
            Account = new CapturedAccountMetadata
            {
                Platform = provider,
                AccountName = accountName,
                AccountHandle = accountHandle
            },
            Media = new CapturedMediaMetadataCore
            {
                ExternalMediaId = sourceId,
                MetadataScrapeDate = clock.GetCurrentInstant(),
                ThumbnailStoragePath = thumbnailStoragePath,
                DurationSeconds = durationSeconds,
                Title = title,
                WebpageUrl = sourceUrl
            },
            Technical = new CapturedMediaTechnicalMetadata
            {
                DurationTicks = durationTicks,
                Format = new CapturedFormatMetadata
                {
                    DurationTicks = durationTicks,
                    FormatLongNames = Path.GetExtension(work.FileName).Trim('.').ToUpperInvariant(),
                    StreamCount = 1
                },
                Streams = width is not null && height is not null
                    ? [new CapturedStreamMetadata
                    {
                        StreamType = "video",
                        IsPrimary = true,
                        CodecName = codec ?? "unknown",
                        CodecLongName = codec ?? "unknown",
                        DurationTicks = durationTicks,
                        Video = new CapturedVideoStreamMetadata { Width = width.Value, Height = height.Value }
                    }]
                    : []
            },
            Captions = MapCaptions(captionRows)
        };
    }

    private static List<CapturedCaptionMetadata> MapCaptions(IReadOnlyList<LocalImportCaptionStoragePath> captionRows)
        => captionRows.Select(c => new CapturedCaptionMetadata
        {
            StoragePath = c.StoragePath,
            CaptionType = string.IsNullOrWhiteSpace(c.CaptionType) ? "subtitles" : c.CaptionType!,
            LanguageCode = string.IsNullOrWhiteSpace(c.LanguageCode) ? "und" : c.LanguageCode!,
            Name = c.Name
        }).ToList();

    private static LocalMediaImportManifestSidecars? ParseSidecars(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var captions = new List<LocalMediaImportCaptionSidecar>();
            if (root.TryGetProperty("captions", out var captionsElement) && captionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var caption in captionsElement.EnumerateArray())
                {
                    var file = ReadElementString(caption, "file");
                    if (string.IsNullOrWhiteSpace(file))
                        continue;
                    captions.Add(new LocalMediaImportCaptionSidecar
                    {
                        File = file,
                        LanguageCode = ReadElementString(caption, "languageCode"),
                        CaptionType = ReadElementString(caption, "captionType"),
                        Name = ReadElementString(caption, "name")
                    });
                }
            }

            return new LocalMediaImportManifestSidecars
            {
                InfoJson = ReadElementString(root, "infoJson"),
                Thumbnail = ReadElementString(root, "thumbnail"),
                Captions = captions
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadJsonString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ReadElementString(doc.RootElement, name);
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadProbeDuration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var duration)
                && double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        catch
        {
        }

        return null;
    }

    private static (int? Width, int? Height, string? Codec) ReadProbeVideo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (ReadElementString(stream, "codec_type") != "video")
                        continue;
                    int? width = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var parsedWidth) ? parsedWidth : null;
                    int? height = stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var parsedHeight) ? parsedHeight : null;
                    return (width, height, ReadElementString(stream, "codec_name"));
                }
            }
        }
        catch
        {
        }

        return (null, null, null);
    }

    private static string? ReadElementString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildSidecarStoragePath(string primaryStoragePath, string sidecarFileName)
    {
        var lastSlash = primaryStoragePath.LastIndexOf('/');
        var directory = lastSlash >= 0 ? primaryStoragePath[..lastSlash] : string.Empty;
        return string.IsNullOrEmpty(directory) ? sidecarFileName : $"{directory}/{sidecarFileName}";
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => bus.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private async Task ImportRepoCall(Func<IImportSessionRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task<T> ImportRepoCall<T>(Func<IImportSessionRepository, Task<T>> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task RepoCall(Func<IDownloadJobsRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task<T> RepoCall<T>(Func<IDownloadJobsRepository, Task<T>> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task MetaRepoCall(Func<IMetadataRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

    private sealed class LocalImportItemFailedException(string errorCode, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public string ErrorCode { get; } = errorCode;
    }

    private sealed record LocalImportCaptionStoragePath(string StoragePath, string? LanguageCode, string? CaptionType, string? Name);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }
}
