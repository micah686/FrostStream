using System.Text;
using System.Text.Json;
using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using DataBridge.Data;
using Conduit.NATS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shared.Database;
using Shared.Imports;
using Shared.Messaging;
using Shared.Metadata;

namespace DataBridge.Flows;

[GenerateFlows]
public class LocalMediaImportFlow(
    IJetStreamPublisher bus,
    IMessageBus messageBus,
    Func<string, IObjectStore> objectStoreFactory,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<LocalMediaImportFlow> logger
) : Flow<LocalMediaImportRequested>
{
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions ManifestJsonOptions = CreateManifestJsonOptions();

    public override async Task Run(LocalMediaImportRequested request)
    {
        var batchId = request.BatchId;
        var batchInstance = batchId.ToString("N");
        var storageKey = string.IsNullOrWhiteSpace(request.StorageKey) ? "default" : request.StorageKey.Trim();
        var sourceRoot = request.SourceRoot.Trim();
        var workerTag = await Capture(() => ResolveWorkerTagAsync(storageKey));

        logger.LogInformation(
            "Local media import flow started for BatchId {BatchId} StorageKey {StorageKey} SourceRoot {SourceRoot} WorkerTag {WorkerTag}",
            batchId,
            storageKey,
            sourceRoot,
            workerTag);

        await Capture(() => ImportRepoCall(r => r.CreateBatchIfMissingAsync(request)));

        LocalMediaImportManifest manifest;
        try
        {
            manifest = await Capture(() => LoadManifestAsync(request));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local media import manifest load failed for BatchId {BatchId}.", batchId);
            await Capture(() => ImportRepoCall(r => r.MarkBatchFailedAsync(batchId, ex.Message)));
            return;
        }

        var validation = LocalMediaImportManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            var error = string.Join(" ", validation.Errors);
            logger.LogWarning("Local media import manifest validation failed for BatchId {BatchId}: {Error}", batchId, error);
            await Capture(() => ImportRepoCall(r => r.MarkBatchFailedAsync(batchId, error)));
            return;
        }

        await Capture(() => ImportRepoCall(r => r.MarkBatchPreparingAsync(batchId, manifest.Items.Count)));
        var createdItems = await Capture(() => ImportRepoCall(r => r.CreateItemsIfMissingAsync(
            batchId,
            BuildItemCreates(manifest, sourceRoot, storageKey))));
        var itemIdsByIndex = createdItems.ToDictionary(x => x.ItemIndex, x => x.ItemId);

        for (var i = 0; i < manifest.Items.Count; i++)
        {
            var itemId = itemIdsByIndex[i];
            await RunItemAsync(
                request,
                manifest.Items[i],
                itemId,
                i,
                sourceRoot,
                storageKey,
                batchInstance,
                workerTag);
        }

        await Capture(() => ImportRepoCall(r => r.CompleteBatchAsync(batchId)));

        logger.LogInformation("Local media import flow finished for BatchId {BatchId}.", batchId);
    }

    private async Task RunItemAsync(
        LocalMediaImportRequested request,
        LocalMediaImportManifestItem item,
        Guid itemId,
        int itemIndex,
        string sourceRoot,
        string storageKey,
        string batchInstance,
        string? workerTag)
    {
        var itemInstance = $"item/{itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        await Capture(() => ImportRepoCall(r => r.MarkItemPreparingAsync(itemId)));

        var prepared = await RunPrepareStepAsync(request, item, itemId, itemIndex, sourceRoot, batchInstance, workerTag);
        if (prepared is null)
            return;

        await Capture(() => ImportRepoCall(r => r.MarkItemPreparedAsync(itemId, prepared)));

        VersionReservation reservation;
        try
        {
            reservation = await Capture(() => RepoCall(r => r.ReserveVersionAsync(new VersionReservationRequest
            {
                JobId = request.BatchId,
                ContentHashXxh128 = prepared.ContentHashXxh128,
                StorageKey = storageKey,
                FileName = prepared.FileName,
                Provider = item.Provider,
                SourceMediaId = item.SourceMediaId,
                SourceLastModified = item.SourceLastModified,
                IngestOrigin = IngestOrigin.LocalImport,
                LinkSourceToDownloadJob = false
            })));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local media import reservation failed for BatchId {BatchId} ItemId {ItemId}.", request.BatchId, itemId);
            await Capture(() => ImportRepoCall(r => r.MarkItemFailedAsync(itemId, "reservation_failed", ex.Message)));
            return;
        }

        if (reservation.ContentAlreadyStored)
        {
            logger.LogInformation(
                "Local media import found existing stored content for BatchId {BatchId} ItemId {ItemId} MediaGuid {MediaGuid}.",
                request.BatchId,
                itemId,
                reservation.MediaGuid);
            await Capture(() => ImportRepoCall(r => r.MarkItemAlreadyImportedAsync(
                itemId,
                reservation.MediaGuid,
                reservation.StoragePath,
                prepared)));
            return;
        }

        await Capture(() => ImportRepoCall(r => r.MarkItemUploadingAsync(itemId, reservation.MediaGuid, reservation.StoragePath)));

        var uploadedObjects = new List<UploadCompleted>();
        try
        {
            var primary = await RunUploadStepAsync(
                request,
                itemId,
                itemInstance,
                causationId: prepared.MessageId,
                tempFileRef: prepared.SourceFileRef,
                inlineContent: null,
                storageKey,
                reservation.StoragePath,
                prepared.ContentHashXxh128,
                UploadArtifactKind.Primary,
                workerTag);
            if (primary is null)
                throw new LocalImportItemFailedException("upload_failed", "Primary media upload failed.");
            uploadedObjects.Add(primary);

            var meta = await RunMetaUploadStepAsync(
                request,
                item,
                itemId,
                itemInstance,
                primary,
                reservation.MediaGuid,
                prepared.ContentHashXxh128,
                storageKey,
                workerTag);
            if (meta is null)
                throw new LocalImportItemFailedException("meta_upload_failed", ".meta sidecar upload failed.");
            uploadedObjects.Add(meta);

            string? infoJsonPath = null;
            if (prepared.InfoJson is { } infoJson)
            {
                var uploaded = await RunPreparedSidecarUploadStepAsync(
                    request,
                    itemId,
                    itemInstance,
                    primary,
                    infoJson,
                    storageKey,
                    UploadArtifactKind.InfoJson,
                    "info-json",
                    workerTag);
                if (uploaded is null)
                    throw new LocalImportItemFailedException("sidecar_upload_failed", "infoJson sidecar upload failed.");
                infoJsonPath = uploaded.StoragePath;
                uploadedObjects.Add(uploaded);
            }

            string? thumbnailPath = null;
            if (prepared.Thumbnail is { } thumbnail)
            {
                var uploaded = await RunPreparedSidecarUploadStepAsync(
                    request,
                    itemId,
                    itemInstance,
                    primary,
                    thumbnail,
                    storageKey,
                    UploadArtifactKind.Thumbnail,
                    "thumbnail",
                    workerTag);
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
                    itemId,
                    itemInstance,
                    primary,
                    caption,
                    storageKey,
                    UploadArtifactKind.Caption,
                    $"caption-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    workerTag);
                if (uploaded is null)
                    throw new LocalImportItemFailedException("sidecar_upload_failed", "caption sidecar upload failed.");

                uploadedObjects.Add(uploaded);
                captionRows.Add(new LocalImportCaptionStoragePath(
                    uploaded.StoragePath,
                    caption.LanguageCode,
                    caption.CaptionType,
                    caption.Name));
            }

            if (item.Metadata is { } metadata)
            {
                var enrichedMetadata = EnrichMetadataWithSidecars(metadata, thumbnailPath, captionRows);
                await RunMetadataWriteStepAsync(
                    request.BatchId,
                    itemId,
                    reservation.MediaGuid,
                    item.Provider,
                    item.SourceMediaId,
                    reservation.IsNewMediaGuid,
                    enrichedMetadata,
                    storageKey);
            }

            var captionStoragePathsJson = captionRows.Count == 0
                ? null
                : JsonSerializer.Serialize(captionRows, ManifestJsonOptions);
            await Capture(() => ImportRepoCall(r => r.MarkItemCompletedAsync(
                itemId,
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
            logger.LogWarning(
                ex,
                "Local media import item failed for BatchId {BatchId} ItemId {ItemId}; compensating.",
                request.BatchId,
                itemId);
            await CompensateAsync(request, item, itemId, itemInstance, reservation, uploadedObjects, workerTag);
            await Capture(() => ImportRepoCall(r => r.MarkItemFailedAsync(itemId, ex.ErrorCode, ex.Message, reservation.MediaGuid, reservation.StoragePath)));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Local media import item failed unexpectedly for BatchId {BatchId} ItemId {ItemId}; compensating.",
                request.BatchId,
                itemId);
            await CompensateAsync(request, item, itemId, itemInstance, reservation, uploadedObjects, workerTag);
            await Capture(() => ImportRepoCall(r => r.MarkItemFailedAsync(itemId, "item_failed", ex.Message, reservation.MediaGuid, reservation.StoragePath)));
        }
    }

    private async Task<LocalImportFilePrepared?> RunPrepareStepAsync(
        LocalMediaImportRequested request,
        LocalMediaImportManifestItem item,
        Guid itemId,
        int itemIndex,
        string sourceRoot,
        string batchInstance,
        string? workerTag)
    {
        var msgId = await Capture(Guid.NewGuid);
        var cmd = new PrepareLocalImportFileCommand
        {
            JobId = request.BatchId,
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            MessageId = msgId,
            OperationKey = $"local-import/{batchInstance}/item/{itemIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}/prepare",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            BatchId = request.BatchId,
            ItemId = itemId,
            SourceRoot = sourceRoot,
            File = item.File,
            Sidecars = item.Sidecars,
            RequiredWorkerTag = workerTag
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? LocalImportSubjects.PrepareLocalImportFileCommand
            : LocalImportSubjects.PrepareLocalImportFileCommandForTag(workerTag);

        await Capture(() => Publish(subject, cmd));
        var result = await Messages.FirstOfTypes<LocalImportFilePrepared, LocalImportFilePrepareFailed>();
        if (result.HasFirst)
            return result.First;

        await Capture(() => ImportRepoCall(r => r.MarkItemFailedAsync(
            itemId,
            result.Second.ErrorCode,
            result.Second.ErrorMessage)));
        return null;
    }

    private async Task<UploadCompleted?> RunUploadStepAsync(
        LocalMediaImportRequested request,
        Guid itemId,
        string itemInstance,
        Guid causationId,
        string? tempFileRef,
        byte[]? inlineContent,
        string storageKey,
        string storagePath,
        string contentHash,
        UploadArtifactKind kind,
        string? workerTag)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var msgId = await Capture(Guid.NewGuid);
            var cmd = new UploadObjectCommand
            {
                JobId = request.BatchId,
                CorrelationId = request.CorrelationId,
                CausationId = causationId,
                MessageId = msgId,
                OperationKey = $"local-import/{request.BatchId:N}/{itemInstance}/upload/{kind.ToString().ToLowerInvariant()}/attempt/{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                TempFileRef = tempFileRef,
                InlineContent = inlineContent,
                RequiredWorkerTag = workerTag,
                StorageKey = storageKey,
                StoragePath = storagePath,
                ContentHashXxh128 = contentHash,
                Kind = kind
            };
            var subject = string.IsNullOrWhiteSpace(workerTag)
                ? DownloadSubjects.UploadObjectCommand
                : DownloadSubjects.UploadObjectCommandForTag(workerTag);

            await Capture(() => Publish(subject, cmd));
            var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
            if (result.HasFirst)
                return result.First;

            logger.LogWarning(
                "Local media import upload failed for BatchId {BatchId} ItemId {ItemId} Kind {Kind} Attempt {Attempt}: {ErrorMessage}",
                request.BatchId,
                itemId,
                kind,
                attempt,
                result.Second.ErrorMessage);
            if (result.Second.FailureKind is FailureKind.Permanent or FailureKind.Cancelled || attempt >= MaxAttempts)
                return null;
        }

        return null;
    }

    private async Task<UploadCompleted?> RunPreparedSidecarUploadStepAsync(
        LocalMediaImportRequested request,
        Guid itemId,
        string itemInstance,
        UploadCompleted primary,
        LocalImportPreparedSidecar sidecar,
        string storageKey,
        UploadArtifactKind kind,
        string operationSuffix,
        string? workerTag)
    {
        var storagePath = BuildSidecarStoragePath(primary.StoragePath, sidecar.FileName);
        return await RunUploadStepAsync(
            request,
            itemId,
            $"{itemInstance}/{operationSuffix}",
            primary.MessageId,
            sidecar.SourceFileRef,
            null,
            storageKey,
            storagePath,
            sidecar.ContentHashXxh128,
            kind,
            workerTag);
    }

    private async Task<UploadCompleted?> RunMetaUploadStepAsync(
        LocalMediaImportRequested request,
        LocalMediaImportManifestItem item,
        Guid itemId,
        string itemInstance,
        UploadCompleted primary,
        Guid mediaGuid,
        string contentHashXxh128,
        string storageKey,
        string? workerTag)
    {
        var metaStoragePath = BuildSidecarStoragePath(primary.StoragePath, $"{mediaGuid:N}.meta");
        var metaContent = new
        {
            mediaGuid = mediaGuid.ToString("D"),
            title = item.Title ?? item.Metadata?.Media.Title,
            contentHashXxh128,
            sourceUrl = item.SourceUrl,
            ingestOrigin = "local_import",
            batchId = request.BatchId.ToString("D"),
            itemId = itemId.ToString("D"),
            relativePath = item.File
        };
        var metaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metaContent, ManifestJsonOptions));
        var metaHash = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(metaBytes));

        return await RunUploadStepAsync(
            request,
            itemId,
            $"{itemInstance}/meta",
            primary.MessageId,
            null,
            metaBytes,
            storageKey,
            metaStoragePath,
            metaHash,
            UploadArtifactKind.Meta,
            workerTag);
    }

    private async Task CompensateAsync(
        LocalMediaImportRequested request,
        LocalMediaImportManifestItem item,
        Guid itemId,
        string itemInstance,
        VersionReservation reservation,
        IReadOnlyList<UploadCompleted> uploadedObjects,
        string? workerTag)
    {
        for (var i = uploadedObjects.Count - 1; i >= 0; i--)
        {
            await DispatchUploadedObjectDeletionAsync(request, uploadedObjects[i], $"{itemInstance}/compensate/{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", workerTag);
            var result = await Messages.FirstOfTypes<UploadedObjectDeleted, UploadedObjectDeleteFailed>();
            if (!result.HasFirst)
            {
                logger.LogWarning(
                    "Local media import compensation delete failed for BatchId {BatchId} ItemId {ItemId} StoragePath {StoragePath}: {ErrorMessage}",
                    request.BatchId,
                    itemId,
                    result.Second.StoragePath,
                    result.Second.ErrorMessage);
            }
        }

        await Capture(() => RepoCall(r => r.DeleteReservedVersionAsync(reservation.MediaGuid, reservation.VersionNum)));
        if (reservation.IsNewMediaGuid)
        {
            await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(
                reservation.MediaGuid,
                item.Provider,
                item.SourceMediaId)));
        }
    }

    private async Task DispatchUploadedObjectDeletionAsync(
        LocalMediaImportRequested request,
        UploadCompleted uploaded,
        string operationSuffix,
        string? workerTag)
    {
        var msgId = await Capture(Guid.NewGuid);
        var deletion = new DeleteUploadedObjectCommand
        {
            JobId = request.BatchId,
            CorrelationId = request.CorrelationId,
            CausationId = uploaded.MessageId,
            MessageId = msgId,
            OperationKey = $"local-import/{request.BatchId:N}/{operationSuffix}/delete-uploaded",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            RequiredWorkerTag = workerTag,
            StorageKey = uploaded.StorageKey,
            StoragePath = uploaded.StoragePath,
            StorageVersion = uploaded.StorageVersion
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.DeleteUploadedObjectCommand
            : DownloadSubjects.DeleteUploadedObjectCommandForTag(workerTag);
        await Capture(() => Publish(subject, deletion));
    }

    private static CapturedMediaMetadata EnrichMetadataWithSidecars(
        CapturedMediaMetadata metadata,
        string? thumbnailStoragePath,
        IReadOnlyList<LocalImportCaptionStoragePath> captionRows)
    {
        var media = thumbnailStoragePath is null
            ? metadata.Media
            : metadata.Media with { ThumbnailStoragePath = thumbnailStoragePath };

        var captions = captionRows.Count == 0
            ? metadata.Captions
            : captionRows.Select(c => new CapturedCaptionMetadata
            {
                StoragePath = c.StoragePath,
                CaptionType = string.IsNullOrWhiteSpace(c.CaptionType) ? "subtitles" : c.CaptionType!,
                LanguageCode = string.IsNullOrWhiteSpace(c.LanguageCode) ? "und" : c.LanguageCode!,
                Name = c.Name
            }).ToList();

        return metadata with
        {
            Media = media,
            Captions = captions
        };
    }

    private async Task RunMetadataWriteStepAsync(
        Guid batchId,
        Guid itemId,
        Guid mediaGuid,
        string? provider,
        string? sourceMediaId,
        bool isNewMediaGuid,
        CapturedMediaMetadata metadata,
        string storageKey)
    {
        try
        {
            await Capture(() => MetaRepoCall(r => r.WriteMetadataAsync(mediaGuid, metadata, storageKey)));
            await Capture(() => PublishMetadataSync(mediaGuid));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Local media import metadata write failed for BatchId {BatchId} ItemId {ItemId} MediaGuid {MediaGuid}.",
                batchId,
                itemId,
                mediaGuid);

            if (isNewMediaGuid)
                await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(mediaGuid, provider, sourceMediaId)));

            throw new LocalImportItemFailedException("metadata_write_failed", ex.Message, ex);
        }
    }

    private async Task<LocalMediaImportManifest> LoadManifestAsync(LocalMediaImportRequested request)
    {
        var objectStore = objectStoreFactory(request.ManifestObjectBucket);
        await using var stream = new MemoryStream();
        await objectStore.GetAsync(request.ManifestObjectKey, stream);
        stream.Position = 0;
        return await JsonSerializer.DeserializeAsync<LocalMediaImportManifest>(stream, ManifestJsonOptions)
               ?? throw new JsonException("Manifest body was empty.");
    }

    private static IReadOnlyList<LocalImportItemCreate> BuildItemCreates(
        LocalMediaImportManifest manifest,
        string sourceRoot,
        string storageKey)
    {
        var creates = new List<LocalImportItemCreate>(manifest.Items.Count);
        for (var i = 0; i < manifest.Items.Count; i++)
        {
            var item = manifest.Items[i];
            LocalImportPathRules.TryNormalizeRelativePath(item.File, out var normalizedPath, out _);
            creates.Add(new LocalImportItemCreate
            {
                ItemIndex = i,
                SourceRoot = sourceRoot,
                RelativePath = normalizedPath,
                StorageKey = storageKey,
                Provider = item.Provider,
                SourceMediaId = item.SourceMediaId,
                SourceLastModified = item.SourceLastModified,
                SourceUrl = item.SourceUrl,
                Title = item.Title ?? item.Metadata?.Media.Title
            });
        }

        return creates;
    }

    private static string BuildSidecarStoragePath(string primaryStoragePath, string sidecarFileName)
    {
        var lastSlash = primaryStoragePath.LastIndexOf('/');
        var directory = lastSlash >= 0 ? primaryStoragePath[..lastSlash] : string.Empty;
        return string.IsNullOrEmpty(directory)
            ? sidecarFileName
            : $"{directory}/{sidecarFileName}";
    }

    private async Task<string?> ResolveWorkerTagAsync(string storageKey)
        => await scopeFactory.WithScopedAsync<DataBridgeDbContext, string?>(async db =>
            await db.StorageConfigs
                .Where(x => x.Key == storageKey)
                .Select(x => x.WorkerTag)
                .FirstOrDefaultAsync());

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => bus.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private async Task PublishMetadataSync(Guid mediaGuid)
    {
        try
        {
            await messageBus.PublishAsync(
                MetadataSyncSubjects.SyncUpsert,
                new MetadataSyncUpsertMessage { MediaGuid = mediaGuid });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed publishing metadata sync upsert for {MediaGuid}.", mediaGuid);
        }
    }

    private async Task ImportRepoCall(Func<ILocalImportRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task<T> ImportRepoCall<T>(Func<ILocalImportRepository, Task<T>> action)
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

    private sealed record LocalImportCaptionStoragePath(
        string StoragePath,
        string? LanguageCode,
        string? CaptionType,
        string? Name);

    private static JsonSerializerOptions CreateManifestJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return options;
    }
}
