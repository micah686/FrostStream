using System.Text.Json;
using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using DataBridge.AudioRenditions;
using DataBridge;
using DataBridge.Data;
using DataBridge.Messaging;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using Shared.Metadata;
using YtDlpSharpLib.Options;

namespace DataBridge.Flows;

[GenerateFlows]
public class DownloadArchiveFlow(
    IJetStreamPublisher bus,
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    DownloadSlotCoordinator slotCoordinator,
    INotificationDispatcher notificationDispatcher,
    ILogger<DownloadArchiveFlow> logger
) : Flow<DownloadRequested>
{
    private const int MaxAttempts = 3;

    public override async Task Run(DownloadRequested request)
    {
        var jobId = request.JobId;
        var jobInstance = jobId.ToString("N");
        var storageKey = request.StorageKey ?? "default";
        MetadataFetched? metadata = null;

        // Resolve the worker tag for this storage key once and capture it so retries
        // replay the same routing decision even if config changes mid-flow.
        var workerTag = await Capture(() => ResolveWorkerTagAsync(storageKey));

        logger.LogInformation(
            "Download flow started for JobId {JobId} CorrelationId {CorrelationId} URL {SourceUrl} StorageKey {StorageKey} ForceDownload {ForceDownload} MediaKind {MediaKind} PresetKey {PresetKey} HasCookieProfile {HasCookieProfile}",
            jobId,
            request.CorrelationId,
            request.SourceUrl,
            storageKey,
            request.ForceDownload,
            request.MediaKind,
            request.PresetKey,
            !string.IsNullOrWhiteSpace(request.CookieSecretPath));

        // Resolve a stored preset -> YtDlpOptions if the request used PresetKey instead
        // of inline options. Mutually-exclusive validation lives at the API layer.
        request = await Capture(() => ResolvePresetAsync(request));

        await Capture(() => Update(jobId, DownloadJobState.Queued));
        if (await Capture(() => IsCancelling(jobId)))
        {
            await Capture(() => Cancel(jobId, "Download cancelled by request."));
            return;
        }

        if (request.ResumeFromHaltedState)
        {
            metadata = await Capture(() => RepoCall(r => r.GetLastMetadataFetchedAsync(jobId)));
            if (metadata is not null)
            {
                logger.LogInformation(
                    "Download flow resuming from recorded metadata for JobId {JobId} Provider {Provider} SourceMediaId {SourceMediaId}.",
                    jobId,
                    metadata.Provider,
                    metadata.SourceMediaId);
                await Capture(() => Update(jobId, DownloadJobState.MetadataResolved));
            }
        }

        // STEP 1: metadata ----------------------------------------------------
        if (metadata is null)
        {
            await Capture(() => Update(jobId, DownloadJobState.MetadataPending));
            metadata = await RunMetadataStep(request, jobInstance, storageKey, workerTag);
            if (metadata is null) return;
            if (await Capture(() => IsCancelling(jobId)))
            {
                await Capture(() => Cancel(jobId, "Download cancelled by request."));
                return;
            }
        }

        // STEP 2: dedupe by source -------------------------------------------
        var sourceCheck = await Capture(() => RepoCall(r =>
            r.CheckSourceVersionAsync(metadata, request.ForceDownload)));
        if (sourceCheck.AlreadyDownloaded && sourceCheck.MediaGuid is { } existingGuid)
        {
            logger.LogInformation(
                "Download flow found existing media for JobId {JobId} MediaGuid {MediaGuid}; marking already downloaded.",
                jobId,
                existingGuid);
            await Capture(() => RepoCall(r => r.MarkAlreadyDownloadedAsync(jobId, existingGuid)));
            return;
        }

        // STEP 2.5: priority gate — wait for download slot --------------------
        if (await Capture(() => IsCancelling(jobId)))
        {
            await Capture(() => Cancel(jobId, "Download cancelled by request."));
            return;
        }
        await Capture(() => Update(jobId, DownloadJobState.DownloadQueued));
        await Capture(() => slotCoordinator.EnqueueAsync(jobId, request.Priority, workerTag, clock.GetCurrentInstant()));
        var slotResult = await Messages.FirstOfTypes<DownloadSlotGranted, DownloadCancelRequested>();
        if (slotResult.HasSecond || await Capture(() => IsCancelling(jobId)))
        {
            if (slotResult.HasFirst)
                await Capture(() => slotCoordinator.ReleaseSlotAsync(workerTag));
            await Capture(() => Cancel(jobId, slotResult.HasSecond ? slotResult.Second.Reason : null));
            return;
        }

        // STEP 3: download ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.DownloadPending));
        var downloaded = await RunDownloadStep(request, jobInstance, workerTag);
        // Release immediately after the download (regardless of outcome) so the next
        // highest-priority job can start while we finish upload/commit for this one.
        await Capture(() => slotCoordinator.ReleaseSlotAsync(workerTag));
        if (downloaded is null) return;

        // STEP 4: reserve version (option-a merge happens here) ---------------
        var reservation = await Capture(() => RepoCall(r => r.ReserveVersionAsync(new VersionReservationRequest
        {
            JobId = jobId,
            ContentHashXxh128 = downloaded.ContentHashXxh128,
            StorageKey = storageKey,
            FileName = downloaded.FileName,
            Provider = metadata.Provider,
            SourceMediaId = metadata.SourceMediaId,
            SourceLastModified = metadata.SourceLastModified
        })));

        if (reservation.ContentAlreadyStored)
        {
            logger.LogInformation(
                "Download flow found existing stored content for JobId {JobId} MediaGuid {MediaGuid} Version {VersionNum}; skipping upload.",
                jobId,
                reservation.MediaGuid,
                reservation.VersionNum);
            // Bytes already in storage under a prior media_guid (or a prior version of this
            // one). Skip upload, clean the temp file we just produced, write metadata, mark AlreadyDownloaded.
            await Capture(() => DispatchTempFileCleanup(request, downloaded.TempFileRef, jobInstance, attempt: 1, workerTag));
            await Message<TempFileDeleted>();
            // Co-located sidecars (info.json, thumbnail, captions) from a duplicate download aren't
            // useful — the prior copy already lives next to the bytes. Just drop the temp files.
            if (downloaded.InfoJsonTempFileRef is { } skippedSidecar)
            {
                await Capture(() => DispatchTempFileCleanup(request, skippedSidecar, jobInstance, attempt: 1, workerTag));
                await Message<TempFileDeleted>();
            }
            if (downloaded.Thumbnail is { } skippedThumb)
            {
                await Capture(() => DispatchTempFileCleanup(request, skippedThumb.TempFileRef, jobInstance, attempt: 1, workerTag));
                await Message<TempFileDeleted>();
            }
            foreach (var skippedCaption in downloaded.Captions)
            {
                await Capture(() => DispatchTempFileCleanup(request, skippedCaption.TempFileRef, jobInstance, attempt: 1, workerTag));
                await Message<TempFileDeleted>();
            }
            // richMeta here already carries no thumbnail/captions (the mapper emits none) — so the
            // dedupe branch writes durable metadata without leaking remote URLs.
            if (metadata.RichMetadata is { } existingRichMeta)
                await RunMetadataWriteStep(jobId, reservation.MediaGuid, reservation.IsNewMediaGuid, metadata.Provider, metadata.SourceMediaId, existingRichMeta, storageKey);
            await Capture(() => PlaylistRepoCall(r => r.TryLinkMediaGuidAsync(jobId, reservation.MediaGuid)));
            await Capture(() => QueueAudioRenditionAsync(request, reservation.MediaGuid, reservation.VersionNum, storageKey));
            await Capture(() => RepoCall(r => r.MarkAlreadyDownloadedAsync(jobId, reservation.MediaGuid)));
            return;
        }

        // STEP 5: upload to the reserved path --------------------------------
        await Capture(() => Update(jobId, DownloadJobState.UploadPending));
        var uploaded = await RunUploadStep(request, downloaded, reservation, storageKey, jobInstance, workerTag);
        if (uploaded is null)
        {
            logger.LogWarning(
                "Upload did not complete for JobId {JobId} MediaGuid {MediaGuid} Version {VersionNum}; deleting reserved version.",
                jobId,
                reservation.MediaGuid,
                reservation.VersionNum);
            // Upload terminally failed. The reserved version row points at a path with no
            // bytes — drop it so a future redownload doesn't reuse the orphan path.
            await Capture(() => RepoCall(r => r.DeleteReservedVersionAsync(reservation.MediaGuid, reservation.VersionNum)));
            if (reservation.IsNewMediaGuid)
                await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(reservation.MediaGuid, metadata.Provider, metadata.SourceMediaId)));
            return;
        }

        // STEP 6: authoritative DB commit ------------------------------------
        try
        {
            await Capture(() => Update(jobId, DownloadJobState.CommitPending));
            await Capture(() => PlaylistRepoCall(r => r.TryLinkMediaGuidAsync(jobId, reservation.MediaGuid)));
            await Capture(() => QueueAudioRenditionAsync(request, reservation.MediaGuid, reservation.VersionNum, storageKey));
            await Capture(() => Update(jobId, DownloadJobState.Completed));
            await notificationDispatcher.NotifyDownloadOutcomeAsync(
                jobId,
                NotificationEventKeys.DownloadCompleted,
                "FrostStream download completed",
                $"Download completed for {request.SourceUrl}");
        }
        catch (Exception commitEx)
        {
            logger.LogError(commitEx,
                "Download flow commit failed for JobId {JobId} MediaGuid {MediaGuid} StoragePath {StoragePath}; starting compensation.",
                jobId,
                reservation.MediaGuid,
                uploaded.StoragePath);
            await Capture(() => DispatchUploadedObjectDeletion(request, uploaded, jobInstance, attempt: 1, workerTag));
            await Capture(() => DispatchTempFileCleanup(request, uploaded.TempFileRef!, jobInstance, attempt: 1, workerTag));
            // Per-media asset sidecars (thumbnail/captions/info.json) are uploaded only AFTER a
            // successful commit, so on commit-failure rollback there are no asset blobs to delete —
            // only the worker-local temp files, which we drop here so they don't orphan. (When a
            // committed version is later removed, its co-located asset blobs become unexpected files
            // and are reclaimed by the generic orphan-cleanup pass.)
            if (downloaded.InfoJsonTempFileRef is { } failedInfoJson)
                await Capture(() => DispatchTempFileCleanup(request, failedInfoJson, jobInstance, attempt: 1, workerTag));
            if (downloaded.Thumbnail is { } failedThumb)
                await Capture(() => DispatchTempFileCleanup(request, failedThumb.TempFileRef, jobInstance, attempt: 1, workerTag));
            foreach (var failedCaption in downloaded.Captions)
                await Capture(() => DispatchTempFileCleanup(request, failedCaption.TempFileRef, jobInstance, attempt: 1, workerTag));
            await Capture(() => RepoCall(r => r.DeleteReservedVersionAsync(reservation.MediaGuid, reservation.VersionNum)));
            if (reservation.IsNewMediaGuid)
                await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(reservation.MediaGuid, metadata.Provider, metadata.SourceMediaId)));
            await Capture(() => RepoCall(r => r.RecordTerminalFailureAsync(
                jobId,
                FailureKind.Permanent,
                code: "commit_failed",
                message: commitEx.Message,
                terminalState: DownloadJobState.FailedPermanent,
                lastPayloadJson: null)));
            await NotifyTerminalFailureAsync(jobId, DownloadJobState.FailedPermanent, "commit_failed", commitEx.Message);
            return;
        }

        // STEP 6b: upload the .info.json sidecar (best-effort, only when yt-dlp produced one) --
        if (downloaded.InfoJsonTempFileRef is { } sidecarTempRef &&
            !string.IsNullOrWhiteSpace(downloaded.InfoJsonFileName) &&
            !string.IsNullOrWhiteSpace(downloaded.InfoJsonContentHashXxh128))
        {
            await RunSidecarUploadStep(
                request,
                uploaded,
                sidecarTempRef,
                downloaded.InfoJsonFileName!,
                downloaded.InfoJsonContentHashXxh128!,
                storageKey,
                jobInstance,
                workerTag);
        }

        // STEP 6b_meta: write .meta sidecar (always — DataBridge-generated, inline content) --
        await RunMetaFileUploadStep(
            request,
            uploaded,
            reservation.MediaGuid,
            metadata.Title,
            downloaded.ContentHashXxh128,
            storageKey,
            jobInstance,
            workerTag);

        // STEP 6b2: upload per-media asset sidecars (thumbnail + captions) and rewrite the
        // metadata storage paths to the uploaded blob keys (best-effort).
        var enrichedMeta = metadata.RichMetadata is { } baseMeta
            ? await RunAssetSidecarUploadsStep(request, uploaded, downloaded, storageKey, jobInstance, baseMeta, workerTag)
            : null;

        // STEP 6c: write rich metadata ----------------------------------------
        if (enrichedMeta is { } richMeta)
            await RunMetadataWriteStep(jobId, reservation.MediaGuid, reservation.IsNewMediaGuid, metadata.Provider, metadata.SourceMediaId, richMeta, storageKey);

        // STEP 7: cleanup -----------------------------------------------------
        var cleanupId = await Capture(Guid.NewGuid);
        var cleanup = new DeleteTempFileCommand
        {
            JobId = jobId,
            CorrelationId = request.CorrelationId,
            CausationId = uploaded.MessageId,
            MessageId = cleanupId,
            OperationKey = $"job/{jobInstance}/cleanup-temp/attempt/1",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            RequiredWorkerTag = workerTag,
            TempFileRef = uploaded.TempFileRef!
        };
        var cleanupSubject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.DeleteTempFileCommand
            : DownloadSubjects.DeleteTempFileCommandForTag(workerTag);
        await Capture(() => Publish(cleanupSubject, cleanup));
        await Message<TempFileDeleted>();

        logger.LogInformation(
            "Download flow completed for JobId {JobId} MediaGuid {MediaGuid} StoragePath {StoragePath}",
            jobId,
            reservation.MediaGuid,
            uploaded.StoragePath);
    }

    private async Task<MetadataFetched?> RunMetadataStep(DownloadRequested request, string jobInstance, string storageKey, string? workerTag)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await Capture(() => RepoCall(r => r.IncrementMetadataAttemptAsync(request.JobId, attempt)));

            var msgId = await Capture(Guid.NewGuid);
            var op = $"job/{jobInstance}/metadata/attempt/{attempt}";
            var cmd = new FetchMetadataCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = msgId,
                OperationKey = op,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                SourceUrl = request.SourceUrl,
                StorageKey = storageKey,
                RequiredWorkerTag = workerTag,
                YtDlpOptions = request.YtDlpOptions,
                CookieSecretPath = request.CookieSecretPath,
                FetchComments = request.FetchComments,
                ResumeFromHaltedState = request.ResumeFromHaltedState
            };
            var subject = string.IsNullOrWhiteSpace(workerTag)
                ? DownloadSubjects.FetchMetadataCommand
                : DownloadSubjects.FetchMetadataCommandForTag(workerTag);
            logger.LogInformation(
                "Download flow dispatching metadata fetch for JobId {JobId} Attempt {Attempt} OperationKey {OperationKey} WorkerTag {WorkerTag}",
                request.JobId,
                attempt,
                op,
                workerTag);
            await Capture(() => Publish(subject, cmd));

            var result = await Messages.FirstOfTypes<MetadataFetched, MetadataFetchFailed>();
            if (result.HasFirst)
            {
                logger.LogInformation(
                    "Download flow metadata fetch completed for JobId {JobId} Attempt {Attempt} Provider {Provider} SourceMediaId {SourceMediaId} Title {Title}",
                    request.JobId,
                    attempt,
                    result.First.Provider,
                    result.First.SourceMediaId,
                    result.First.Title);
                return result.First;
            }

            var failure = result.Second;
            logger.LogWarning(
                "Download flow metadata fetch failed for JobId {JobId} Attempt {Attempt} FailureKind {FailureKind} ErrorCode {ErrorCode} Provider {Provider} HaltProviderDownloads {HaltProviderDownloads} ErrorMessage {ErrorMessage}",
                request.JobId,
                attempt,
                failure.FailureKind,
                failure.ErrorCode,
                failure.Provider,
                failure.HaltProviderDownloads,
                failure.ErrorMessage);
            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                await Capture(() => Fail(request, failure, terminal));
                return null;
            }
        }
        return null;
    }

    private async Task<DownloadCompleted?> RunDownloadStep(DownloadRequested request, string jobInstance, string? workerTag)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await Capture(() => RepoCall(r => r.IncrementDownloadAttemptAsync(request.JobId, attempt)));

            var msgId = await Capture(Guid.NewGuid);
            var op = $"job/{jobInstance}/download/attempt/{attempt}";
            var cmd = new DownloadVideoCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = msgId,
                OperationKey = op,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                SourceUrl = request.SourceUrl,
                RequiredWorkerTag = workerTag,
                MediaKind = request.MediaKind,
                AudioFormat = request.AudioFormat,
                YtDlpOptions = request.YtDlpOptions,
                CookieSecretPath = request.CookieSecretPath,
                ResumeFromHaltedState = request.ResumeFromHaltedState
            };
            var subject = string.IsNullOrWhiteSpace(workerTag)
                ? DownloadSubjects.DownloadVideoCommand
                : DownloadSubjects.DownloadVideoCommandForTag(workerTag);
            logger.LogInformation(
                "Download flow dispatching video download for JobId {JobId} Attempt {Attempt} OperationKey {OperationKey} WorkerTag {WorkerTag}",
                request.JobId,
                attempt,
                op,
                workerTag);
            await Capture(() => Publish(subject, cmd));

            var result = await Messages.FirstOfTypes<DownloadCompleted, DownloadFailed>();
            if (result.HasFirst)
            {
                logger.LogInformation(
                    "Download flow video download completed for JobId {JobId} Attempt {Attempt} TempFileRef {TempFileRef} SizeBytes {FileSizeBytes} ContentHash {ContentHashXxh128}",
                    request.JobId,
                    attempt,
                    result.First.TempFileRef,
                    result.First.FileSizeBytes,
                    result.First.ContentHashXxh128);
                if (await Capture(() => IsCancelling(request.JobId)))
                {
                    await Capture(() => DispatchTempFileCleanup(request, result.First.TempFileRef, jobInstance, attempt, workerTag));
                    await Message<TempFileDeleted>();
                    await Capture(() => Cancel(request.JobId, "Download cancelled by request."));
                    return null;
                }
                return result.First;
            }

            var failure = result.Second;
            logger.LogWarning(
                "Download flow video download failed for JobId {JobId} Attempt {Attempt} FailureKind {FailureKind} ErrorCode {ErrorCode} Provider {Provider} HaltProviderDownloads {HaltProviderDownloads} ErrorMessage {ErrorMessage} TempFileRef {TempFileRef}",
                request.JobId,
                attempt,
                failure.FailureKind,
                failure.ErrorCode,
                failure.Provider,
                failure.HaltProviderDownloads,
                failure.ErrorMessage,
                failure.TempFileRef);
            if (!string.IsNullOrEmpty(failure.TempFileRef))
            {
                await Capture(() => DispatchTempFileCleanup(request, failure.TempFileRef!, jobInstance, attempt, workerTag));
            }

            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                if (terminal == DownloadJobState.Cancelled)
                    await Capture(() => Cancel(request.JobId, failure.ErrorMessage));
                else
                    await Capture(() => Fail(request, failure, terminal));
                return null;
            }
        }
        return null;
    }

    private async Task<UploadCompleted?> RunUploadStep(
        DownloadRequested request,
        DownloadCompleted downloaded,
        VersionReservation reservation,
        string storageKey,
        string jobInstance,
        string? workerTag)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await Capture(() => RepoCall(r => r.IncrementUploadAttemptAsync(request.JobId, attempt)));

            var msgId = await Capture(Guid.NewGuid);
            var op = $"job/{jobInstance}/upload/attempt/{attempt}";
            var cmd = new UploadObjectCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = downloaded.MessageId,
                MessageId = msgId,
                OperationKey = op,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                TempFileRef = downloaded.TempFileRef,
                RequiredWorkerTag = workerTag,
                StorageKey = storageKey,
                StoragePath = reservation.StoragePath,
                ContentHashXxh128 = downloaded.ContentHashXxh128
            };
            var subject = string.IsNullOrWhiteSpace(workerTag)
                ? DownloadSubjects.UploadObjectCommand
                : DownloadSubjects.UploadObjectCommandForTag(workerTag);
            logger.LogInformation(
                "Download flow dispatching upload for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath} OperationKey {OperationKey} WorkerTag {WorkerTag}",
                request.JobId,
                attempt,
                storageKey,
                reservation.StoragePath,
                op,
                workerTag);
            await Capture(() => Publish(subject, cmd));

            var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
            if (result.HasFirst)
            {
                logger.LogInformation(
                    "Download flow upload completed for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath} SizeBytes {ContentLengthBytes}",
                    request.JobId,
                    attempt,
                    result.First.StorageKey,
                    result.First.StoragePath,
                    result.First.ContentLengthBytes);
                return result.First;
            }

            var failure = result.Second;
            logger.LogWarning(
                "Download flow upload failed for JobId {JobId} Attempt {Attempt} FailureKind {FailureKind} ErrorMessage {ErrorMessage} TempFileRef {TempFileRef}",
                request.JobId,
                attempt,
                failure.FailureKind,
                failure.ErrorMessage,
                failure.TempFileRef);
            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                await Capture(() => DispatchTempFileCleanup(request, downloaded.TempFileRef, jobInstance, attempt, workerTag));
                await Capture(() => Fail(request, failure, terminal));
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Uploads the yt-dlp <c>.info.json</c> sidecar to a path co-located with the primary
    /// upload (same directory, sidecar's own filename). Best-effort: a failure here logs
    /// a warning, cleans the sidecar temp file, and lets the flow proceed — the video
    /// itself is already durable, and the metadata it contains is already in the database.
    /// </summary>
    private async Task RunSidecarUploadStep(
        DownloadRequested request,
        UploadCompleted primary,
        string sidecarTempRef,
        string sidecarFileName,
        string sidecarContentHash,
        string storageKey,
        string jobInstance,
        string? workerTag)
    {
        var sidecarStoragePath = BuildSidecarStoragePath(primary.StoragePath, sidecarFileName);
        var msgId = await Capture(Guid.NewGuid);
        var op = $"job/{jobInstance}/upload-sidecar/info-json";
        var cmd = new UploadObjectCommand
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            CausationId = primary.MessageId,
            MessageId = msgId,
            OperationKey = op,
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            TempFileRef = sidecarTempRef,
            RequiredWorkerTag = workerTag,
            StorageKey = storageKey,
            StoragePath = sidecarStoragePath,
            ContentHashXxh128 = sidecarContentHash,
            Kind = UploadArtifactKind.InfoJson
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.UploadObjectCommand
            : DownloadSubjects.UploadObjectCommandForTag(workerTag);
        logger.LogInformation(
            "Download flow dispatching info.json sidecar upload for JobId {JobId} StoragePath {StoragePath}",
            request.JobId,
            sidecarStoragePath);
        await Capture(() => Publish(subject, cmd));

        var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
        if (result.HasFirst)
        {
            logger.LogInformation(
                "Download flow info.json sidecar uploaded for JobId {JobId} StoragePath {StoragePath} SizeBytes {ContentLengthBytes}",
                request.JobId,
                result.First.StoragePath,
                result.First.ContentLengthBytes);
        }
        else
        {
            // Sidecar uploads aren't worth failing a job over — the bytes are already
            // stored and the metadata lives in the database. Log and proceed.
            logger.LogWarning(
                "Download flow info.json sidecar upload failed for JobId {JobId}: {ErrorMessage}",
                request.JobId,
                result.Second.ErrorMessage);
        }

        await Capture(() => DispatchTempFileCleanup(request, sidecarTempRef, jobInstance, attempt: 1, workerTag));
        await Message<TempFileDeleted>();
    }

    /// <summary>
    /// Generates and uploads a DataBridge-authored <c>.meta</c> sidecar co-located with the
    /// primary upload. The file contains title, content hash, media GUID, and original URL so
    /// that the object can be correlated back to its database record after a storage migration
    /// or path rename. Best-effort: failure logs a warning but does not fail the job.
    /// </summary>
    private async Task RunMetaFileUploadStep(
        DownloadRequested request,
        UploadCompleted primary,
        Guid mediaGuid,
        string? title,
        string contentHashXxh128,
        string storageKey,
        string jobInstance,
        string? workerTag)
    {
        var metaFileName = $"{mediaGuid:N}.meta";
        var metaStoragePath = BuildSidecarStoragePath(primary.StoragePath, metaFileName);

        var metaContent = new
        {
            mediaGuid = mediaGuid.ToString("D"),
            title,
            contentHashXxh128,
            originalUrl = request.SourceUrl
        };
        var metaBytes = System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(metaContent, new JsonSerializerOptions { WriteIndented = false }));
        var metaHash = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(metaBytes));

        var msgId = await Capture(Guid.NewGuid);
        var cmd = new UploadObjectCommand
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            CausationId = primary.MessageId,
            MessageId = msgId,
            OperationKey = $"job/{jobInstance}/upload-sidecar/meta",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            InlineContent = metaBytes,
            RequiredWorkerTag = workerTag,
            StorageKey = storageKey,
            StoragePath = metaStoragePath,
            ContentHashXxh128 = metaHash,
            Kind = UploadArtifactKind.Meta
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.UploadObjectCommand
            : DownloadSubjects.UploadObjectCommandForTag(workerTag);
        logger.LogInformation(
            "Download flow dispatching .meta sidecar upload for JobId {JobId} StoragePath {StoragePath}",
            request.JobId,
            metaStoragePath);
        await Capture(() => Publish(subject, cmd));

        var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
        if (result.HasFirst)
        {
            logger.LogInformation(
                "Download flow .meta sidecar uploaded for JobId {JobId} StoragePath {StoragePath}",
                request.JobId,
                result.First.StoragePath);
        }
        else
        {
            logger.LogWarning(
                "Download flow .meta sidecar upload failed for JobId {JobId}: {ErrorMessage}",
                request.JobId,
                result.Second.ErrorMessage);
        }
    }

    /// <summary>
    /// Uploads the per-media thumbnail and caption sidecars co-located with the primary upload
    /// (best-effort), then returns <paramref name="richMeta"/> with the thumbnail storage path and
    /// caption rows rewritten to the uploaded blob keys. Each sidecar temp file is cleaned after.
    /// </summary>
    private async Task<CapturedMediaMetadata> RunAssetSidecarUploadsStep(
        DownloadRequested request,
        UploadCompleted primary,
        DownloadCompleted downloaded,
        string storageKey,
        string jobInstance,
        CapturedMediaMetadata richMeta,
        string? workerTag)
    {
        string? thumbnailStoragePath = null;
        if (downloaded.Thumbnail is { } thumb)
        {
            thumbnailStoragePath = BuildSidecarStoragePath(primary.StoragePath, thumb.FileName);
            await RunAssetSidecarUploadStep(request, primary, thumb, thumbnailStoragePath, storageKey, UploadArtifactKind.Thumbnail, "thumbnail", jobInstance, workerTag);
        }

        var captionRows = new List<CapturedCaptionMetadata>(downloaded.Captions.Count);
        for (var i = 0; i < downloaded.Captions.Count; i++)
        {
            var caption = downloaded.Captions[i];
            var captionPath = BuildSidecarStoragePath(primary.StoragePath, caption.FileName);
            await RunAssetSidecarUploadStep(request, primary, caption, captionPath, storageKey, UploadArtifactKind.Caption, $"caption-{i}", jobInstance, workerTag);
            captionRows.Add(new CapturedCaptionMetadata
            {
                StoragePath = captionPath,
                // The downloaded filename doesn't distinguish manual vs auto-generated captions;
                // default to "subtitles".
                CaptionType = "subtitles",
                LanguageCode = string.IsNullOrWhiteSpace(caption.LanguageCode) ? "und" : caption.LanguageCode!,
                Name = null,
                TextContent = caption.ParsedText
            });
        }

        return richMeta with
        {
            Media = richMeta.Media with { ThumbnailStoragePath = thumbnailStoragePath },
            Captions = captionRows
        };
    }

    /// <summary>
    /// Uploads a single per-media asset sidecar (thumbnail/caption) to a path co-located with the
    /// primary upload. Best-effort: a failure logs a warning and proceeds — the video is already
    /// durable and the metadata write tolerates a missing asset. The temp file is cleaned after.
    /// </summary>
    private async Task RunAssetSidecarUploadStep(
        DownloadRequested request,
        UploadCompleted primary,
        SidecarFileRef sidecar,
        string storagePath,
        string storageKey,
        UploadArtifactKind kind,
        string opSuffix,
        string jobInstance,
        string? workerTag)
    {
        var msgId = await Capture(Guid.NewGuid);
        var cmd = new UploadObjectCommand
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            CausationId = primary.MessageId,
            MessageId = msgId,
            OperationKey = $"job/{jobInstance}/upload-sidecar/{opSuffix}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            TempFileRef = sidecar.TempFileRef,
            RequiredWorkerTag = workerTag,
            StorageKey = storageKey,
            StoragePath = storagePath,
            ContentHashXxh128 = sidecar.ContentHashXxh128,
            Kind = kind
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.UploadObjectCommand
            : DownloadSubjects.UploadObjectCommandForTag(workerTag);
        logger.LogInformation(
            "Download flow dispatching {Kind} sidecar upload for JobId {JobId} StoragePath {StoragePath}",
            kind,
            request.JobId,
            storagePath);
        await Capture(() => Publish(subject, cmd));

        var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
        if (!result.HasFirst)
        {
            logger.LogWarning(
                "Download flow {Kind} sidecar upload failed for JobId {JobId} StoragePath {StoragePath}: {ErrorMessage}",
                kind,
                request.JobId,
                storagePath,
                result.Second.ErrorMessage);
        }

        await Capture(() => DispatchTempFileCleanup(request, sidecar.TempFileRef, jobInstance, attempt: 1, workerTag));
        await Message<TempFileDeleted>();
    }

    /// <summary>
    /// Derives the sidecar's final path from the primary's path. Keeps the same directory
    /// (e.g. <c>archives/{guid}/v{n}/</c>) and substitutes the sidecar's filename.
    /// </summary>
    private static string BuildSidecarStoragePath(string primaryStoragePath, string sidecarFileName)
    {
        var lastSlash = primaryStoragePath.LastIndexOf('/');
        var directory = lastSlash >= 0 ? primaryStoragePath[..lastSlash] : string.Empty;
        return string.IsNullOrEmpty(directory)
            ? sidecarFileName
            : $"{directory}/{sidecarFileName}";
    }

    private static DownloadJobState? TerminalFailureForStep<TFailure>(TFailure failure, int attempt)
        where TFailure : IFlowMessage
    {
        var (kind, haltProviderDownloads) = failure switch
        {
            MetadataFetchFailed m => (m.FailureKind, m.HaltProviderDownloads),
            DownloadFailed d => (d.FailureKind, d.HaltProviderDownloads),
            UploadFailed u => (u.FailureKind, false),
            _ => (FailureKind.Unknown, false)
        };

        if (haltProviderDownloads)
            return DownloadJobState.ProviderHalted;
        if (kind is FailureKind.Cancelled)
            return DownloadJobState.Cancelled;
        if (kind is FailureKind.Permanent)
            return DownloadJobState.FailedPermanent;
        if (attempt >= MaxAttempts)
            return DownloadJobState.FailedTransient;
        return null;
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => bus.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private async Task DispatchTempFileCleanup(DownloadRequested request, string tempFileRef, string jobInstance, int attempt, string? workerTag)
    {
        var cleanup = new DeleteTempFileCommand
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"job/{jobInstance}/cleanup-temp/attempt/{attempt}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = attempt,
            RequiredWorkerTag = workerTag,
            TempFileRef = tempFileRef
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.DeleteTempFileCommand
            : DownloadSubjects.DeleteTempFileCommandForTag(workerTag);
        await Publish(subject, cleanup);
    }

    private async Task DispatchUploadedObjectDeletion(DownloadRequested request, UploadCompleted uploaded, string jobInstance, int attempt, string? workerTag)
    {
        var deletion = new DeleteUploadedObjectCommand
        {
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            CausationId = uploaded.MessageId,
            MessageId = Guid.NewGuid(),
            OperationKey = $"job/{jobInstance}/cleanup-uploaded/attempt/{attempt}",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = attempt,
            RequiredWorkerTag = workerTag,
            StorageKey = uploaded.StorageKey,
            StoragePath = uploaded.StoragePath,
            StorageVersion = uploaded.StorageVersion
        };
        var subject = string.IsNullOrWhiteSpace(workerTag)
            ? DownloadSubjects.DeleteUploadedObjectCommand
            : DownloadSubjects.DeleteUploadedObjectCommandForTag(workerTag);
        await Publish(subject, deletion);
    }

    private async Task Update(Guid jobId, DownloadJobState state)
    {
        logger.LogInformation("Download flow state update for JobId {JobId}: {State}", jobId, state);
        await RepoCall(r => r.UpdateStateAsync(jobId, state));
    }

    private async Task Fail<TFailure>(DownloadRequested request, TFailure failure, DownloadJobState terminalState)
        where TFailure : IFlowMessage
    {
        var (kind, code, message) = failure switch
        {
            MetadataFetchFailed m => (m.FailureKind, m.ErrorCode, m.ErrorMessage),
            DownloadFailed d => (d.FailureKind, d.ErrorCode, d.ErrorMessage),
            UploadFailed u => (u.FailureKind, u.ErrorCode, u.ErrorMessage),
            _ => (FailureKind.Unknown, (string?)null, "unknown failure")
        };
        logger.LogWarning(
            "Download flow terminal failure for JobId {JobId}: State {TerminalState} FailureKind {FailureKind} ErrorCode {ErrorCode} ErrorMessage {ErrorMessage}",
            request.JobId,
            terminalState,
            kind,
            code,
            message);
        await RepoCall(r => r.RecordTerminalFailureAsync(request.JobId, kind, code, message, terminalState, lastPayloadJson: null));
        await NotifyTerminalFailureAsync(request.JobId, terminalState, code, message);

        if (terminalState == DownloadJobState.ProviderHalted && request.SourceKind is not DownloadSourceKind.Direct)
        {
            var retryAt = clock.GetCurrentInstant().Plus(Duration.FromHours(2));
            logger.LogInformation(
                "Scheduling provider-halt retry for JobId {JobId} at {RetryAt} SourceKind {SourceKind}.",
                request.JobId,
                retryAt,
                request.SourceKind);
            await RepoCall(r => r.ScheduleProviderHaltRetryAsync(request.JobId, retryAt));
        }
    }

    private async Task Cancel(Guid jobId, string? message)
    {
        logger.LogInformation("Download flow cancelled for JobId {JobId}: {Message}", jobId, message);
        await RepoCall(r => r.MarkCancelledAsync(jobId, message));
    }

    private async Task<bool> IsCancelling(Guid jobId)
    {
        var (state, _) = await RepoCall(r => r.GetJobStateAndStorageKeyAsync(jobId));
        return state == DownloadJobState.Cancelling || state == DownloadJobState.Cancelled;
    }

    private async Task RunMetadataWriteStep(
        Guid jobId,
        Guid mediaGuid,
        bool isNewMediaGuid,
        string? provider,
        string? sourceMediaId,
        CapturedMediaMetadata richMeta,
        string storageKey)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                logger.LogInformation(
                    "Writing rich metadata for JobId {JobId} MediaGuid {MediaGuid} Attempt {Attempt}",
                    jobId,
                    mediaGuid,
                    attempt);
                await Capture(() => MetaRepoCall(r => r.WriteMetadataAsync(mediaGuid, richMeta, storageKey)));
                await Capture(() => PublishMetadataSync(mediaGuid));
                logger.LogInformation(
                    "Rich metadata written for JobId {JobId} MediaGuid {MediaGuid} Attempt {Attempt}",
                    jobId,
                    mediaGuid,
                    attempt);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex,
                    "Rich metadata write failed for JobId {JobId} MediaGuid {MediaGuid} Attempt {Attempt}; retrying.",
                    jobId,
                    mediaGuid,
                    attempt);
            }
            catch (Exception metaEx)
            {
                logger.LogError(metaEx,
                    "Rich metadata write failed terminally for JobId {JobId} MediaGuid {MediaGuid}",
                    jobId,
                    mediaGuid);
                // Metadata write exhausted retries — compensate and fail the job.
                if (isNewMediaGuid)
                    await Capture(() => RepoCall(r => r.DeleteNewMediaGuidAsync(mediaGuid, provider, sourceMediaId)));
                await Capture(() => RepoCall(r => r.RecordTerminalFailureAsync(
                    jobId,
                    FailureKind.Permanent,
                    code: "metadata_write_failed",
                    message: metaEx.Message,
                    terminalState: DownloadJobState.FailedPermanent,
                    lastPayloadJson: null)));
                await NotifyTerminalFailureAsync(jobId, DownloadJobState.FailedPermanent, "metadata_write_failed", metaEx.Message);
                return;
            }
        }
    }

    private Task NotifyTerminalFailureAsync(Guid jobId, DownloadJobState terminalState, string? code, string message)
    {
        if (terminalState is not (DownloadJobState.FailedPermanent or DownloadJobState.DeadLettered or DownloadJobState.ProviderHalted))
            return Task.CompletedTask;

        var eventKey = terminalState switch
        {
            DownloadJobState.ProviderHalted => NotificationEventKeys.DownloadProviderHalted,
            DownloadJobState.DeadLettered => NotificationEventKeys.DownloadDeadLettered,
            _ => NotificationEventKeys.DownloadFailedPermanent
        };
        var subject = terminalState switch
        {
            DownloadJobState.ProviderHalted => "FrostStream provider halted a download",
            DownloadJobState.DeadLettered => "FrostStream download dead-lettered",
            _ => "FrostStream download failed"
        };
        var body = string.IsNullOrWhiteSpace(code)
            ? $"Download job {jobId} entered {terminalState}: {message}"
            : $"Download job {jobId} entered {terminalState} ({code}): {message}";

        return notificationDispatcher.NotifyDownloadOutcomeAsync(jobId, eventKey, subject, body);
    }

    private async Task<string?> ResolveWorkerTagAsync(string storageKey)
        => await scopeFactory.WithScopedAsync<DataBridgeDbContext, string?>(async db =>
            await db.StorageConfigs
                .Where(x => x.Key == storageKey)
                .Select(x => x.WorkerTag)
                .FirstOrDefaultAsync());

    private async Task MetaRepoCall(Func<IMetadataRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

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

    private async Task PlaylistRepoCall(Func<IPlaylistsRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task<T> PlaylistRepoCall<T>(Func<IPlaylistsRepository, Task<T>> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task QueueAudioRenditionAsync(DownloadRequested request, Guid mediaGuid, int sourceVersion, string storageKey)
    {
        var preference = await PlaylistRepoCall(r => r.GetAudioPreferenceForJobAsync(request.JobId));
        var shouldEncode = preference is { EncodeForPlaylist: true } || request.EncodeAudioRendition;
        if (!shouldEncode)
            return;

        var audioFormat = preference?.AudioFormat ?? request.AudioRenditionFormat;
        var audioStorageKey = preference?.StorageKey ?? storageKey;

        var rendition = await scopeFactory.WithScopedAsync<IAudioRenditionRepository, AudioRenditionDto?>(
            repo => repo.CreateIfMissingAsync(mediaGuid, audioFormat, audioStorageKey, sourceVersion));
        if (rendition is null || rendition.Status == AudioRenditionStatus.Ready)
            return;

        await bus.PublishAsync(
            BackgroundJobSubjects.AudioRenditionEncodeRequest,
            new AudioRenditionEncodeRequested
            {
                RenditionId = rendition.RenditionId,
                MediaGuid = rendition.MediaGuid,
                SourceVersion = rendition.SourceVersion,
                Format = rendition.Format
            },
            messageId: rendition.RenditionId.ToString("N"));
    }

    private async Task RepoCall(Func<IDownloadJobsRepository, Task> action)
        => await scopeFactory.WithScopedAsync(action);

    private async Task<T> RepoCall<T>(Func<IDownloadJobsRepository, Task<T>> action)
        => await scopeFactory.WithScopedAsync(action);

    /// <summary>
    /// If <see cref="DownloadRequested.PresetKey"/> is set and inline
    /// <see cref="DownloadRequested.YtDlpOptions"/> are not, look up the preset and
    /// hydrate the request. Missing presets are logged and the flow proceeds with
    /// no options (rather than failing the job) — the API layer is responsible for
    /// rejecting unknown keys upfront, this is a safety net for races.
    /// </summary>
    private async Task<DownloadRequested> ResolvePresetAsync(DownloadRequested request)
    {
        if (request.YtDlpOptions is not null || string.IsNullOrWhiteSpace(request.PresetKey))
            return request;

        var preset = await scopeFactory.WithScopedAsync<IOptionPresetsRepository, OptionPresetEntity?>(
            presets => presets.GetByKeyAsync(request.PresetKey));
        if (preset is null)
        {
            logger.LogWarning(
                "Download flow could not find preset '{PresetKey}' for JobId {JobId}; proceeding with no options.",
                request.PresetKey,
                request.JobId);
            return request;
        }

        try
        {
            var options = JsonSerializer.Deserialize<YtDlpOptions>(preset.YtDlpOptionsJson);
            return request with { YtDlpOptions = options };
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "Download flow failed to deserialize preset '{PresetKey}' for JobId {JobId}; proceeding with no options.",
                request.PresetKey,
                request.JobId);
            return request;
        }
    }
}
