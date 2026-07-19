using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using Conduit.NATS;
using DataBridge.AudioRenditions;
using DataBridge.Data;
using DataBridge.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Database;
using Shared.Messaging;
using Shared.Metadata;
using YtDlpSharpLib.Options;

namespace DataBridge.Flows;

/// <summary>
/// Download Flow V2. A Cleipnir instance represents exactly one immutable run and is never
/// restarted. User Start creates another instance with a new RunId under the same JobId.
/// </summary>
[GenerateFlows]
public sealed class DownloadJobV2Flow(
    IJetStreamPublisher bus,
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    INotificationDispatcher notificationDispatcher,
    ILogger<DownloadJobV2Flow> logger) : Flow<DownloadRunRequest>
{
    internal const int MaxStageAttempts = 3;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public override async Task Run(DownloadRunRequest run)
    {
        var request = await Capture(() => ResolvePresetAsync(run.Request));
        var jobId = request.JobId;
        var runId = run.RunId;
        var storageKey = request.StorageKey ?? "default";
        var workerTag = await Capture(() => ResolveWorkerTagAsync(storageKey));

        logger.LogInformation(
            "Download V2 run started JobId {JobId} RunId {RunId} Run {RunNumber} CorrelationId {CorrelationId} URL {Url}",
            jobId, runId, run.RunNumber, request.CorrelationId, request.SourceUrl);

        var metadata = await RunMetadataAsync(run, request, storageKey, workerTag);
        if (metadata is null)
            return;

        if (await StopIfRequestedAsync(run, "Stopped after metadata."))
            return;

        await Capture(() => V2(r => r.TransitionAsync(jobId, runId, DownloadJobStatus.Running,
            DownloadStage.DuplicateCheck, DownloadStageStatus.Running)));
        var sourceDecision = await Capture(() => Jobs(r => r.CheckSourceVersionAsync(metadata, request.ForceDownload)));
        if (sourceDecision.AlreadyDownloaded && sourceDecision.MediaGuid is { } existingMediaGuid)
        {
            if (await StopIfRequestedAsync(run, "Stopped during duplicate check."))
                return;
            var settled = await Capture(() => V2(r => r.MarkAlreadyDownloadedAsync(
                jobId, runId, existingMediaGuid, metadata.Provider, metadata.SourceMediaId,
                metadata.SourceLastModified)));
            if (!settled)
            {
                await SettlePreArtifactStopAsync(run, "Stopped during duplicate settlement.");
                return;
            }
            await Capture(() => QueueAudioRenditionAsync(request, existingMediaGuid, version: null, storageKey));
            return;
        }

        if (await StopIfRequestedAsync(run, "Stopped before media acquisition."))
            return;

        await Capture(() => V2(r => r.TransitionAsync(jobId, runId, DownloadJobStatus.Running,
            DownloadStage.WaitingForWorker, DownloadStageStatus.Pending)));
        var downloaded = await RunMediaAcquireAsync(run, request, workerTag);
        if (downloaded is null)
            return;

        if (await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
        {
            await CleanupAllTempFilesAsync(run, request, downloaded, workerTag);
            await Capture(() => V2(r => r.MarkStoppedAsync(jobId, runId, "Stopped after media acquisition.")));
            return;
        }
        await Capture(() => Jobs(r => r.ApplyDownloadCompletedAsync(jobId, downloaded)));
        foreach (var warning in downloaded.Warnings)
        {
            await Capture(() => V2(r => r.RecordWarningAsync(
                jobId,
                runId,
                DownloadStage.MediaAcquire,
                "yt-dlp-sidecars",
                warning.Code,
                warning.Message)));
        }

        var reservation = await Capture(() => Jobs(r => r.ReserveVersionAsync(new VersionReservationRequest
        {
            JobId = jobId,
            ContentHashXxh128 = downloaded.ContentHashXxh128,
            StorageKey = storageKey,
            FileName = downloaded.FileName,
            Provider = metadata.Provider,
            SourceMediaId = metadata.SourceMediaId,
            SourceLastModified = metadata.SourceLastModified,
            PersistSourceMapping = false,
            LinkSourceToDownloadJob = false
        })));

        if (reservation.ContentAlreadyStored)
        {
            await CleanupAllTempFilesAsync(run, request, downloaded, workerTag);
            if (await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
            {
                await Capture(() => V2(r => r.MarkStoppedAsync(jobId, runId,
                    "User stopped the run while settling an already-stored version.")));
                return;
            }
            var settled = await Capture(() => V2(r => r.MarkAlreadyDownloadedAsync(
                jobId, runId, reservation.MediaGuid, metadata.Provider, metadata.SourceMediaId,
                metadata.SourceLastModified)));
            if (!settled)
            {
                await SettlePreArtifactStopAsync(run, "Stopped during duplicate settlement.");
                return;
            }
            await Capture(() => QueueAudioRenditionAsync(
                request, reservation.MediaGuid, reservation.VersionNum, storageKey));
            return;
        }

        var primary = await RunUploadAsync(
            run, request, workerTag, DownloadStage.PrimaryMediaUpload, "primary", UploadArtifactKind.Primary,
            required: true, downloaded.TempFileRef, inlineContent: null, storageKey, reservation.StoragePath,
            downloaded.ContentHashXxh128);
        if (!primary.Succeeded)
        {
            await CompensateAsync(
                run, request, workerTag, downloaded, reservation, metadata,
                primary.FailureKind ?? FailureKind.Permanent,
                primary.FailureCode ?? "primary_upload_failed",
                primary.FailureMessage);
            return;
        }

        await Capture(() => Jobs(r => r.CommitUploadAsync(jobId, primary.Upload!)));
        await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(run, DownloadStage.PrimaryMediaUpload,
            "primary", UploadArtifactKind.Primary, required: true, primary.Upload!))));

        if (await StopIfRequestedAsync(run, "Stopped after primary upload.", async () =>
            await CompensateAsync(
                run, request, workerTag, downloaded, reservation, metadata,
                FailureKind.Stopped, "user_stopped", "User stopped the run.")))
            return;

        if (downloaded.InfoJsonTempFileRef is { } infoTemp
            && downloaded.InfoJsonFileName is { Length: > 0 } infoName
            && downloaded.InfoJsonContentHashXxh128 is { Length: > 0 } infoHash)
        {
            var info = await RunUploadAsync(run, request, workerTag, DownloadStage.InfoJsonUpload, "info-json",
                UploadArtifactKind.InfoJson, required: false, infoTemp, null, storageKey,
                SidecarPath(primary.Upload!.StoragePath, infoName), infoHash);
            if (info.Stopped || await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    FailureKind.Stopped, "user_stopped", "User stopped the run.");
                return;
            }
            if (info.Fatal)
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    info.FailureKind ?? FailureKind.Interrupted,
                    info.FailureCode ?? "info_json_upload_failed",
                    info.FailureMessage);
                return;
            }
            if (info.Succeeded)
            {
                await Capture(() => Jobs(r => r.ApplySidecarUploadCompletedAsync(jobId, info.Upload!)));
                await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(run, DownloadStage.InfoJsonUpload,
                    "info-json", UploadArtifactKind.InfoJson, required: false, info.Upload!))));
            }
        }
        else
        {
            await Capture(() => V2(r => r.TransitionAsync(jobId, runId, DownloadJobStatus.Running,
                DownloadStage.InfoJsonUpload, DownloadStageStatus.Skipped)));
        }

        string? commentsStoragePath = null;
        if (downloaded.Comments is { } commentsSidecar)
        {
            var comments = await RunUploadAsync(run, request, workerTag, DownloadStage.InfoJsonUpload, "comments",
                UploadArtifactKind.Comments, required: false, commentsSidecar.TempFileRef, null, storageKey,
                SidecarPath(primary.Upload!.StoragePath, commentsSidecar.FileName), commentsSidecar.ContentHashXxh128);
            if (comments.Stopped || await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    FailureKind.Stopped, "user_stopped", "User stopped the run.");
                return;
            }
            if (comments.Fatal)
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    comments.FailureKind ?? FailureKind.Interrupted,
                    comments.FailureCode ?? "comments_upload_failed",
                    comments.FailureMessage);
                return;
            }
            if (comments.Succeeded)
            {
                commentsStoragePath = comments.Upload!.StoragePath;
                await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(run, DownloadStage.InfoJsonUpload,
                    "comments", UploadArtifactKind.Comments, required: false, comments.Upload!))));
            }
        }

        var metaBytes = BuildMetaBytes(request, reservation.MediaGuid, metadata.Title, downloaded.ContentHashXxh128);
        var metaHash = Convert.ToHexStringLower(XxHash128.Hash(metaBytes));
        var meta = await RunUploadAsync(run, request, workerTag, DownloadStage.MetaSidecarUpload, "meta",
            UploadArtifactKind.Meta, required: true, tempFileRef: null, inlineContent: metaBytes, storageKey,
            SidecarPath(primary.Upload!.StoragePath, $"{reservation.MediaGuid:N}.meta"), metaHash);
        if (!meta.Succeeded)
        {
            await CompensateAsync(
                run, request, workerTag, downloaded, reservation, metadata,
                meta.FailureKind ?? FailureKind.Permanent,
                meta.FailureCode ?? "meta_upload_failed",
                meta.FailureMessage);
            return;
        }
        await Capture(() => Jobs(r => r.ApplyMetaUploadCompletedAsync(jobId, meta.Upload!)));
        await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(run, DownloadStage.MetaSidecarUpload,
            "meta", UploadArtifactKind.Meta, required: true, meta.Upload!))));

        string? thumbnailPath = null;
        if (downloaded.Thumbnail is { } thumbnail)
        {
            var thumb = await RunUploadAsync(run, request, workerTag, DownloadStage.ThumbnailUpload, "thumbnail",
                UploadArtifactKind.Thumbnail, required: false, thumbnail.TempFileRef, null, storageKey,
                SidecarPath(primary.Upload.StoragePath, thumbnail.FileName), thumbnail.ContentHashXxh128);
            if (thumb.Stopped || await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    FailureKind.Stopped, "user_stopped", "User stopped the run.");
                return;
            }
            if (thumb.Fatal)
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    thumb.FailureKind ?? FailureKind.Interrupted,
                    thumb.FailureCode ?? "thumbnail_upload_failed",
                    thumb.FailureMessage);
                return;
            }
            if (thumb.Succeeded)
            {
                thumbnailPath = thumb.Upload!.StoragePath;
                await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(run, DownloadStage.ThumbnailUpload,
                    "thumbnail", UploadArtifactKind.Thumbnail, required: false, thumb.Upload!))));
            }
        }
        else
        {
            await Capture(() => V2(r => r.TransitionAsync(jobId, runId, DownloadJobStatus.Running,
                DownloadStage.ThumbnailUpload, DownloadStageStatus.Skipped)));
        }

        var captionRows = new List<CapturedCaptionMetadata>();
        for (var index = 0; index < downloaded.Captions.Count; index++)
        {
            var caption = downloaded.Captions[index];
            var artifactKey = $"caption:{index}:{caption.LanguageCode ?? "und"}";
            var uploadedCaption = await RunUploadAsync(run, request, workerTag, DownloadStage.CaptionUpload, artifactKey,
                UploadArtifactKind.Caption, required: false, caption.TempFileRef, null, storageKey,
                SidecarPath(primary.Upload.StoragePath, caption.FileName), caption.ContentHashXxh128);
            if (uploadedCaption.Stopped || await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    FailureKind.Stopped, "user_stopped", "User stopped the run.");
                return;
            }
            if (uploadedCaption.Fatal)
            {
                await CompensateAsync(
                    run, request, workerTag, downloaded, reservation, metadata,
                    uploadedCaption.FailureKind ?? FailureKind.Interrupted,
                    uploadedCaption.FailureCode ?? "caption_upload_failed",
                    uploadedCaption.FailureMessage);
                return;
            }
            if (!uploadedCaption.Succeeded)
                continue;
            await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(run, DownloadStage.CaptionUpload,
                artifactKey, UploadArtifactKind.Caption, required: false, uploadedCaption.Upload!))));
            captionRows.Add(new CapturedCaptionMetadata
            {
                StoragePath = uploadedCaption.Upload!.StoragePath,
                CaptionType = "subtitles",
                LanguageCode = string.IsNullOrWhiteSpace(caption.LanguageCode) ? "und" : caption.LanguageCode!,
                Name = null
            });
        }

        var enrichedMetadata = metadata.RichMetadata is null
            ? null
            : metadata.RichMetadata with
            {
                Media = metadata.RichMetadata.Media with { ThumbnailStoragePath = thumbnailPath },
                Captions = captionRows
            };
        var richMetadata = await RunRichMetadataWriteAsync(
            run, metadata with { RichMetadata = enrichedMetadata }, reservation, storageKey, commentsStoragePath);
        if (!richMetadata.Succeeded)
        {
            await CompensateAsync(
                run, request, workerTag, downloaded, reservation, metadata,
                richMetadata.FailureKind,
                richMetadata.FailureCode,
                richMetadata.FailureMessage);
            return;
        }

        await CleanupAllTempFilesAsync(run, request, downloaded, workerTag);
        if (await Capture(() => V2(r => r.IsStopRequestedAsync(jobId, runId))))
        {
            await CompensateAsync(
                run, request, workerTag, downloaded, reservation, metadata,
                FailureKind.Stopped, "user_stopped", "User stopped the run during cleanup.");
            return;
        }

        var finalized = await RunFinalizeAsync(run, reservation, metadata);
        if (!finalized.Succeeded)
        {
            await CompensateAsync(
                run, request, workerTag, downloaded, reservation, metadata,
                finalized.FailureKind,
                finalized.FailureCode,
                finalized.FailureMessage);
            return;
        }

        await Capture(() => QueueAudioRenditionAsync(
            request, reservation.MediaGuid, reservation.VersionNum, storageKey));
        await Capture(() => NotifyCompletionAsync(jobId, request.SourceUrl));

        logger.LogInformation("Download V2 run completed JobId {JobId} RunId {RunId}", jobId, runId);
    }

    private async Task<MetadataFetched?> RunMetadataAsync(
        DownloadRunRequest run, DownloadRequested request, string storageKey, string? workerTag)
    {
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.Metadata, attempt);
            var operationKey = Operation(run, DownloadStage.Metadata, attempt);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, operationKey))))
            {
                await SettlePreArtifactStopAsync(run, "Stopped before metadata dispatch.");
                return null;
            }
            var command = new FetchMetadataCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = execution.DispatchId,
                OperationKey = operationKey,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                Execution = execution,
                SourceUrl = request.SourceUrl,
                StorageKey = storageKey,
                RequiredWorkerTag = workerTag,
                YtDlpOptions = request.YtDlpOptions,
                CookieSecretPath = request.CookieSecretPath,
                FetchComments = request.FetchComments
            };
            await Capture(() => Publish(Tagged(DownloadSubjects.FetchMetadataCommand, workerTag), command));
            // A stop request changes the durable job gate and cancels the Worker. Wait for the
            // Worker result so an in-flight write cannot race compensation.
            var result = await Messages.FirstOfTypes<MetadataFetched, MetadataFetchFailed>();
            if (result.HasFirst)
            {
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                return result.First;
            }
            if (!await HandleFailureAsync(run, execution, result.Second.FailureKind, result.Second.ErrorCode,
                    result.Second.ErrorMessage, result.Second.HaltProviderDownloads, result.Second.Provider))
                return null;
        }
        return null;
    }

    private async Task<DownloadCompleted?> RunMediaAcquireAsync(DownloadRunRequest run, DownloadRequested request, string? workerTag)
    {
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.MediaAcquire, attempt);
            var operationKey = Operation(run, DownloadStage.MediaAcquire, attempt);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, operationKey))))
            {
                await SettlePreArtifactStopAsync(run, "Stopped before media dispatch.");
                return null;
            }
            var command = new DownloadVideoCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = execution.DispatchId,
                OperationKey = operationKey,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                Execution = execution,
                SourceUrl = request.SourceUrl,
                RequiredWorkerTag = workerTag,
                MediaKind = request.MediaKind,
                AudioFormat = request.AudioFormat,
                YtDlpOptions = request.YtDlpOptions,
                CookieSecretPath = request.CookieSecretPath
            };
            await Capture(() => Publish(Tagged(DownloadSubjects.DownloadVideoCommand, workerTag), command));
            var result = await Messages.FirstOfTypes<DownloadCompleted, DownloadFailed>();
            if (result.HasFirst)
            {
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                return result.First;
            }
            if (!await HandleMediaFailureAsync(run, request, workerTag, execution, result.Second))
                return null;
        }
        return null;
    }

    private async Task<bool> HandleMediaFailureAsync(
        DownloadRunRequest run,
        DownloadRequested request,
        string? workerTag,
        DownloadExecutionIdentity execution,
        DownloadFailed failure)
    {
        var stopped = await Capture(() => V2(r => r.IsStopRequestedAsync(execution.JobId, execution.RunId)))
                      || failure.FailureKind is FailureKind.Stopped or FailureKind.Cancelled;
        if (stopped)
        {
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Stopped,
                failure.ErrorCode ?? "user_stopped", failure.ErrorMessage)));
            if (failure.TempFileRef is { Length: > 0 } stoppedTemp)
                await RunTempCleanupAsync(run, request, workerTag, $"media-attempt:{execution.Attempt}", stoppedTemp);
            await Capture(() => V2(r => r.MarkStoppedAsync(
                execution.JobId, execution.RunId, failure.ErrorMessage)));
            return false;
        }

        if (failure.HaltProviderDownloads)
        {
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.ProviderBlocked,
                failure.ErrorCode ?? "provider_blocked", failure.ErrorMessage)));
            var circuitProvider = string.IsNullOrWhiteSpace(failure.Provider)
                ? null
                : failure.Provider.Trim().ToLowerInvariant();
            if (circuitProvider is not null)
                await Capture(() => V2(r => r.OpenProviderCircuitAsync(circuitProvider, failure.ErrorMessage)));
            if (failure.TempFileRef is { Length: > 0 } blockedTemp)
                await RunTempCleanupAsync(run, request, workerTag, $"media-attempt:{execution.Attempt}", blockedTemp);
            if (await Capture(() => V2(r => r.IsStopRequestedAsync(execution.JobId, execution.RunId))))
            {
                await Capture(() => V2(r => r.MarkStoppedAsync(
                    execution.JobId, execution.RunId, failure.ErrorMessage)));
                return false;
            }
            await Capture(() => V2(r => r.FailRunAsync(
                execution.JobId,
                execution.RunId,
                FailureKind.ProviderBlocked,
                failure.ErrorCode ?? "provider_blocked",
                circuitProvider is null
                    ? failure.ErrorMessage
                    : $"The provider circuit for '{circuitProvider}' is open. {failure.ErrorMessage}")));
            return false;
        }

        var canRetry = Retryable(failure.FailureKind) && execution.Attempt < MaxStageAttempts;
        if (canRetry)
        {
            var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(
                execution, failure.FailureKind, failure.ErrorCode, failure.ErrorMessage)));
            if (!scheduled)
            {
                await SettlePreArtifactStopAsync(run, "Stopped before the next media attempt.");
                return false;
            }
            if (failure.TempFileRef is { Length: > 0 } retryTemp)
                await RunTempCleanupAsync(run, request, workerTag, $"media-attempt:{execution.Attempt}", retryTemp);
            if (await Capture(() => V2(r => r.IsStopRequestedAsync(execution.JobId, execution.RunId))))
            {
                await Capture(() => V2(r => r.MarkStoppedAsync(
                    execution.JobId, execution.RunId, "Stopped while cleaning a failed media attempt.")));
                return false;
            }
            await Capture(() => Task.Delay(RetryDelay));
            return true;
        }

        await Capture(() => V2(r => r.FailStageAttemptAsync(
            execution, failure.FailureKind, failure.ErrorCode ?? "stage_failed", failure.ErrorMessage)));
        if (failure.TempFileRef is { Length: > 0 } failedTemp)
            await RunTempCleanupAsync(run, request, workerTag, $"media-attempt:{execution.Attempt}", failedTemp);
        if (await Capture(() => V2(r => r.IsStopRequestedAsync(execution.JobId, execution.RunId))))
        {
            await Capture(() => V2(r => r.MarkStoppedAsync(
                execution.JobId, execution.RunId, failure.ErrorMessage)));
            return false;
        }
        await Capture(() => V2(r => r.FailRunAsync(
            execution.JobId,
            execution.RunId,
            failure.FailureKind,
            failure.ErrorCode ?? "stage_failed",
            failure.ErrorMessage)));
        return false;
    }

    private async Task<UploadOutcome> RunUploadAsync(
        DownloadRunRequest run,
        DownloadRequested request,
        string? workerTag,
        DownloadStage stage,
        string artifactKey,
        UploadArtifactKind kind,
        bool required,
        string? tempFileRef,
        byte[]? inlineContent,
        string storageKey,
        string storagePath,
        string contentHash)
    {
        await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
        {
            JobId = request.JobId,
            RunId = run.RunId,
            Stage = stage,
            ArtifactKey = artifactKey,
            Kind = kind,
            Required = required,
            Status = DownloadArtifactStatus.Pending,
            TempFileRef = tempFileRef,
            StorageKey = storageKey,
            StoragePath = storagePath,
            ContentHashXxh128 = contentHash
        })));

        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, stage, attempt, artifactKey);
            var operationKey = Operation(run, stage, attempt, artifactKey);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, operationKey))))
                return new UploadOutcome(
                    null, false, true, false, FailureKind.Stopped, "run_not_current",
                    "Run is no longer current.");
            await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
            {
                JobId = request.JobId,
                RunId = run.RunId,
                Stage = stage,
                ArtifactKey = artifactKey,
                Kind = kind,
                Required = required,
                Status = DownloadArtifactStatus.Uploading,
                TempFileRef = tempFileRef,
                StorageKey = storageKey,
                StoragePath = storagePath,
                ContentHashXxh128 = contentHash
            })));
            var command = new UploadObjectCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = execution.DispatchId,
                OperationKey = operationKey,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                Execution = execution,
                TempFileRef = tempFileRef,
                InlineContent = inlineContent,
                RequiredWorkerTag = workerTag,
                StorageKey = storageKey,
                StoragePath = storagePath,
                ContentHashXxh128 = contentHash,
                VerifyHashWhileStreaming = true,
                Kind = kind
            };
            await Capture(() => Publish(Tagged(ArtifactStorageSubjects.UploadObjectCommand, workerTag), command));
            var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
            if (result.HasFirst)
            {
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                await Capture(() => V2(r => r.UpsertArtifactAsync(ToArtifact(
                    run, stage, artifactKey, kind, required, result.First))));
                return new UploadOutcome(result.First, true, false, false, null, null, null);
            }

            var failure = result.Second;
            if (await Capture(() => V2(r => r.IsStopRequestedAsync(request.JobId, run.RunId))))
            {
                await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Stopped,
                    "user_stopped", "The run was stopped while the artifact operation was active.")));
                return new UploadOutcome(
                    null, false, true, false, FailureKind.Stopped, "user_stopped",
                    "The run was stopped.");
            }
            var canRetry = Retryable(failure.FailureKind) && attempt < MaxStageAttempts;
            if (canRetry)
            {
                var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(execution, failure.FailureKind,
                    failure.ErrorCode, failure.ErrorMessage)));
                if (!scheduled)
                    return new UploadOutcome(
                        null, false, true, false, FailureKind.Stopped, "run_not_current",
                        "The run is no longer executable.");
                await Capture(() => Task.Delay(RetryDelay));
                continue;
            }

            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, failure.FailureKind,
                failure.ErrorCode, failure.ErrorMessage)));

            // Losing a Worker lease is a run-level interruption, not an optional-artifact
            // warning. The caller compensates every durable artifact and leaves the job Failed.
            if (failure.FailureKind == FailureKind.Interrupted)
            {
                await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
                {
                    JobId = request.JobId,
                    RunId = run.RunId,
                    Stage = stage,
                    ArtifactKey = artifactKey,
                    Kind = kind,
                    Required = required,
                    Status = DownloadArtifactStatus.Residual,
                    TempFileRef = tempFileRef,
                    StorageKey = storageKey,
                    StoragePath = storagePath,
                    ContentHashXxh128 = contentHash,
                    WarningCode = failure.ErrorCode ?? "worker_interrupted",
                    WarningMessage = failure.ErrorMessage
                })));
                return new UploadOutcome(
                    null, false, false, true, failure.FailureKind,
                    failure.ErrorCode ?? "worker_interrupted", failure.ErrorMessage);
            }

            if (!required)
            {
                await Capture(() => V2(r => r.RecordWarningAsync(request.JobId, run.RunId, stage, artifactKey,
                    failure.ErrorCode ?? "optional_artifact_failed", failure.ErrorMessage)));
                await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
                {
                    JobId = request.JobId,
                    RunId = run.RunId,
                    Stage = stage,
                    ArtifactKey = artifactKey,
                    Kind = kind,
                    Required = false,
                    Status = DownloadArtifactStatus.Warning,
                    TempFileRef = tempFileRef,
                    StorageKey = storageKey,
                    StoragePath = storagePath,
                    ContentHashXxh128 = contentHash,
                    WarningCode = failure.ErrorCode,
                    WarningMessage = failure.ErrorMessage
                })));
                return new UploadOutcome(
                    null, false, false, false, failure.FailureKind,
                    failure.ErrorCode ?? "optional_artifact_failed", failure.ErrorMessage);
            }

            await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
            {
                JobId = request.JobId,
                RunId = run.RunId,
                Stage = stage,
                ArtifactKey = artifactKey,
                Kind = kind,
                Required = true,
                Status = DownloadArtifactStatus.Failed,
                TempFileRef = tempFileRef,
                StorageKey = storageKey,
                StoragePath = storagePath,
                ContentHashXxh128 = contentHash,
                WarningCode = failure.ErrorCode,
                WarningMessage = failure.ErrorMessage
            })));
            return new UploadOutcome(
                null, false, false, true, failure.FailureKind,
                failure.ErrorCode ?? "required_artifact_failed", failure.ErrorMessage);
        }
        return new UploadOutcome(
            null, false, false, true, FailureKind.Transient,
            "stage_attempts_exhausted", "Stage attempts exhausted.");
    }

    private async Task<RequiredStageOutcome> RunRichMetadataWriteAsync(
        DownloadRunRequest run, MetadataFetched metadata, VersionReservation reservation, string storageKey,
        string? commentsStoragePath)
    {
        if (metadata.RichMetadata is null)
        {
            var missing = await NewExecutionAsync(run, DownloadStage.RichMetadataWrite, 1);
            var op = Operation(run, DownloadStage.RichMetadataWrite, 1);
            await Capture(() => V2(r => r.BeginStageAttemptAsync(missing, op)));
            await Capture(() => V2(r => r.FailStageAttemptAsync(missing, FailureKind.Permanent,
                "rich_metadata_missing", "The Worker did not return the required rich metadata.")));
            return RequiredStageOutcome.Failed(
                FailureKind.Permanent,
                "rich_metadata_missing",
                "The Worker did not return the required rich metadata.");
        }

        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.RichMetadataWrite, attempt);
            var op = Operation(run, DownloadStage.RichMetadataWrite, attempt);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, op))))
                return RequiredStageOutcome.Failed(
                    FailureKind.Stopped, "run_not_current", "The run is no longer executable.");
            try
            {
                await Capture(() => WriteRichMetadataAsync(
                    reservation.MediaGuid, metadata.RichMetadata, storageKey, commentsStoragePath));
                await Capture(() => PublishMetadataSync(reservation.MediaGuid));
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                return RequiredStageOutcome.Success;
            }
            catch (Exception ex) when (attempt < MaxStageAttempts)
            {
                var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(
                    execution, FailureKind.Transient, "rich_metadata_write_failed", ex.Message)));
                if (!scheduled)
                    return RequiredStageOutcome.Failed(
                        FailureKind.Stopped, "run_not_current", "The run is no longer executable.");
                await Capture(() => Task.Delay(RetryDelay));
            }
            catch (Exception ex)
            {
                await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Transient,
                    "rich_metadata_write_failed", ex.Message)));
                return RequiredStageOutcome.Failed(
                    FailureKind.Transient, "rich_metadata_write_failed", ex.Message);
            }
        }
        return RequiredStageOutcome.Failed(
            FailureKind.Transient,
            "rich_metadata_write_failed",
            "Required rich metadata could not be written after three attempts.");
    }

    private async Task<RequiredStageOutcome> RunFinalizeAsync(
        DownloadRunRequest run,
        VersionReservation reservation,
        MetadataFetched metadata)
    {
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.Finalize, attempt);
            var op = Operation(run, DownloadStage.Finalize, attempt);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, op))))
                return RequiredStageOutcome.Failed(
                    FailureKind.Stopped, "run_not_current", "The run is no longer executable.");
            try
            {
                var finalized = await Capture(() => V2(r => r.FinalizeRunAsync(
                    execution,
                    reservation.MediaGuid,
                    metadata.Provider,
                    metadata.SourceMediaId,
                    metadata.SourceLastModified)));
                return finalized
                    ? RequiredStageOutcome.Success
                    : RequiredStageOutcome.Failed(
                        await Capture(() => V2(r => r.IsStopRequestedAsync(
                            run.Request.JobId, run.RunId)))
                            ? FailureKind.Stopped
                            : FailureKind.Interrupted,
                        "finalize_rejected",
                        "The final commit was rejected because the run stopped or was no longer current.");
            }
            catch (Exception ex) when (attempt < MaxStageAttempts)
            {
                var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(
                    execution, FailureKind.Transient, "finalize_failed", ex.Message)));
                if (!scheduled)
                    return RequiredStageOutcome.Failed(
                        FailureKind.Stopped, "run_not_current", "The run is no longer executable.");
                await Capture(() => Task.Delay(RetryDelay));
            }
            catch (Exception ex)
            {
                await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Transient,
                    "finalize_failed", ex.Message)));
                return RequiredStageOutcome.Failed(FailureKind.Transient, "finalize_failed", ex.Message);
            }
        }
        return RequiredStageOutcome.Failed(
            FailureKind.Transient,
            "finalize_failed",
            "The final database commit failed after three attempts.");
    }

    private async Task<bool> CleanupAllTempFilesAsync(
        DownloadRunRequest run, DownloadRequested request, DownloadCompleted downloaded, string? workerTag)
    {
        var succeeded = true;
        var files = new List<(string Key, string Path)> { ("primary", downloaded.TempFileRef) };
        if (downloaded.InfoJsonTempFileRef is { } info) files.Add(("info-json", info));
        if (downloaded.Comments is { } commentsFile) files.Add(("comments", commentsFile.TempFileRef));
        if (downloaded.Thumbnail is { } thumb) files.Add(("thumbnail", thumb.TempFileRef));
        files.AddRange(downloaded.Captions.Select((x, i) => ($"caption:{i}", x.TempFileRef)));
        foreach (var file in files.DistinctBy(x => x.Path, StringComparer.Ordinal))
            succeeded &= await RunTempCleanupAsync(run, request, workerTag, file.Key, file.Path);
        return succeeded;
    }

    private async Task<bool> RunTempCleanupAsync(
        DownloadRunRequest run, DownloadRequested request, string? workerTag, string artifactKey, string tempFileRef)
    {
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.Cleanup, attempt, artifactKey);
            var op = Operation(run, DownloadStage.Cleanup, attempt, artifactKey);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, op))))
                return false;
            var command = new DeleteTempFileCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = execution.DispatchId,
                OperationKey = op,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                Execution = execution,
                RequiredWorkerTag = workerTag,
                TempFileRef = tempFileRef
            };
            await Capture(() => Publish(Tagged(ArtifactStorageSubjects.DeleteTempFileCommand, workerTag), command));
            var result = await Messages.FirstOfTypes<TempFileDeleted, TempFileDeleteFailed>();
            if (result.HasFirst)
            {
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                return true;
            }
            if (attempt < MaxStageAttempts && Retryable(result.Second.FailureKind))
            {
                var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(
                    execution, result.Second.FailureKind,
                    "temp_cleanup_failed", result.Second.ErrorMessage)));
                if (!scheduled)
                    return false;
                await Capture(() => Task.Delay(RetryDelay));
                continue;
            }
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, result.Second.FailureKind,
                "temp_cleanup_failed", result.Second.ErrorMessage)));
            await Capture(() => V2(r => r.RecordWarningAsync(request.JobId, run.RunId, DownloadStage.Cleanup,
                artifactKey, "temp_cleanup_failed", result.Second.ErrorMessage)));
            return false;
        }
        return false;
    }

    private async Task CompensateAsync(
        DownloadRunRequest run,
        DownloadRequested request,
        string? workerTag,
        DownloadCompleted downloaded,
        VersionReservation reservation,
        MetadataFetched metadata,
        FailureKind failureKind,
        string failureCode,
        string? reason)
    {
        await Capture(() => V2(r => r.TransitionAsync(request.JobId, run.RunId, DownloadJobStatus.Compensating,
            DownloadStage.Compensation, DownloadStageStatus.Running)));

        var complete = true;
        var artifacts = await Capture(() => V2(r => r.ListCompensatableArtifactsAsync(request.JobId, run.RunId)));
        foreach (var artifact in artifacts
                     .DistinctBy(x => (x.StorageKey, x.StoragePath, x.StorageVersion)))
            complete &= await RunObjectCompensationAsync(run, request, workerTag, artifact);

        complete &= await CleanupAllTempFilesAsync(run, request, downloaded, workerTag);
        if (!reservation.ContentAlreadyStored)
            complete &= await RunDatabaseCompensationAsync(run, "reserved-version",
                () => Jobs(r => r.DeleteReservedVersionAsync(reservation.MediaGuid, reservation.VersionNum)));
        if (reservation.IsNewMediaGuid)
            complete &= await RunDatabaseCompensationAsync(run, "new-media",
                () => Jobs(r => r.DeleteNewMediaGuidAsync(reservation.MediaGuid, metadata.Provider, metadata.SourceMediaId)));

        if (!complete)
        {
            await Capture(() => V2(r => r.FailRunAsync(request.JobId, run.RunId, FailureKind.Permanent,
                "compensation_incomplete",
                "The run failed and one or more side effects could not be removed. Review the residual artifact warnings.")));
            return;
        }

        if (await Capture(() => V2(r => r.IsStopRequestedAsync(request.JobId, run.RunId))))
            await Capture(() => V2(r => r.MarkStoppedAsync(request.JobId, run.RunId, reason)));
        else
            await Capture(() => V2(r => r.FailRunAsync(
                request.JobId,
                run.RunId,
                failureKind,
                failureCode,
                reason ?? "A required download stage failed.")));
    }

    private async Task<bool> RunObjectCompensationAsync(
        DownloadRunRequest run, DownloadRequested request, string? workerTag, DownloadArtifactSnapshot artifact)
    {
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.Compensation, attempt, artifact.ArtifactKey);
            var op = Operation(run, DownloadStage.Compensation, attempt, artifact.ArtifactKey);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, op))))
                return false;
            var command = new DeleteUploadedObjectCommand
            {
                JobId = request.JobId,
                CorrelationId = request.CorrelationId,
                CausationId = request.MessageId,
                MessageId = execution.DispatchId,
                OperationKey = op,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                Execution = execution,
                RequiredWorkerTag = workerTag,
                StorageKey = artifact.StorageKey!,
                StoragePath = artifact.StoragePath!,
                StorageVersion = artifact.StorageVersion
            };
            await Capture(() => Publish(Tagged(ArtifactStorageSubjects.DeleteUploadedObjectCommand, workerTag), command));
            var result = await Messages.FirstOfTypes<UploadedObjectDeleted, UploadedObjectDeleteFailed>();
            if (result.HasFirst)
            {
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
                {
                    JobId = request.JobId,
                    RunId = run.RunId,
                    Stage = artifact.Stage,
                    ArtifactKey = artifact.ArtifactKey,
                    Kind = artifact.Kind,
                    Required = artifact.Required,
                    Status = DownloadArtifactStatus.Deleted,
                    TempFileRef = artifact.TempFileRef,
                    StorageKey = artifact.StorageKey,
                    StoragePath = artifact.StoragePath,
                    StorageVersion = artifact.StorageVersion,
                    ContentHashXxh128 = artifact.ContentHashXxh128,
                    SizeBytes = artifact.SizeBytes
                })));
                return true;
            }
            if (attempt < MaxStageAttempts && Retryable(result.Second.FailureKind))
            {
                var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(
                    execution, result.Second.FailureKind,
                    "object_delete_failed", result.Second.ErrorMessage)));
                if (!scheduled)
                    return false;
                await Capture(() => Task.Delay(RetryDelay));
                continue;
            }
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, result.Second.FailureKind,
                "object_delete_failed", result.Second.ErrorMessage)));
            await Capture(() => V2(r => r.RecordWarningAsync(request.JobId, run.RunId, DownloadStage.Compensation,
                artifact.ArtifactKey, "residual_object", result.Second.ErrorMessage)));
            await Capture(() => V2(r => r.UpsertArtifactAsync(new DownloadArtifactSnapshot
            {
                JobId = request.JobId,
                RunId = run.RunId,
                Stage = artifact.Stage,
                ArtifactKey = artifact.ArtifactKey,
                Kind = artifact.Kind,
                Required = artifact.Required,
                Status = DownloadArtifactStatus.Residual,
                TempFileRef = artifact.TempFileRef,
                StorageKey = artifact.StorageKey,
                StoragePath = artifact.StoragePath,
                StorageVersion = artifact.StorageVersion,
                ContentHashXxh128 = artifact.ContentHashXxh128,
                SizeBytes = artifact.SizeBytes,
                WarningCode = "residual_object",
                WarningMessage = result.Second.ErrorMessage
            })));
            return false;
        }
        return false;
    }

    private async Task<bool> RunDatabaseCompensationAsync(
        DownloadRunRequest run, string artifactKey, Func<Task> action)
    {
        for (var attempt = 1; attempt <= MaxStageAttempts; attempt++)
        {
            var execution = await NewExecutionAsync(run, DownloadStage.Compensation, attempt, artifactKey);
            var op = Operation(run, DownloadStage.Compensation, attempt, artifactKey);
            if (!await Capture(() => V2(r => r.BeginStageAttemptAsync(execution, op))))
                return false;
            try
            {
                await Capture(action);
                await Capture(() => V2(r => r.CompleteStageAttemptAsync(execution)));
                return true;
            }
            catch (Exception ex) when (attempt < MaxStageAttempts)
            {
                var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(
                    execution, FailureKind.Transient, "database_compensation_failed", ex.Message)));
                if (!scheduled)
                    return false;
                await Capture(() => Task.Delay(RetryDelay));
            }
            catch (Exception ex)
            {
                await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Transient,
                    "database_compensation_failed", ex.Message)));
                await Capture(() => V2(r => r.RecordWarningAsync(run.Request.JobId, run.RunId,
                    DownloadStage.Compensation, artifactKey, "residual_database_side_effect", ex.Message)));
                return false;
            }
        }
        return false;
    }

    private async Task<bool> HandleFailureAsync(
        DownloadRunRequest run,
        DownloadExecutionIdentity execution,
        FailureKind kind,
        string? code,
        string message,
        bool haltProvider,
        string? provider)
    {
        if (await Capture(() => V2(r => r.IsStopRequestedAsync(execution.JobId, execution.RunId))))
        {
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Stopped,
                code ?? "user_stopped", message)));
            await Capture(() => V2(r => r.MarkStoppedAsync(execution.JobId, execution.RunId, message)));
            return false;
        }

        if (kind is FailureKind.Stopped or FailureKind.Cancelled)
        {
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.Stopped,
                code ?? "user_stopped", message)));
            await Capture(() => V2(r => r.MarkStoppedAsync(execution.JobId, execution.RunId, message)));
            return false;
        }

        if (haltProvider)
        {
            await Capture(() => V2(r => r.FailStageAttemptAsync(execution, FailureKind.ProviderBlocked,
                code ?? "provider_blocked", message)));
            var circuitProvider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
            if (circuitProvider is not null)
                await Capture(() => V2(r => r.OpenProviderCircuitAsync(circuitProvider, message)));
            await Capture(() => V2(r => r.FailRunAsync(execution.JobId, execution.RunId, FailureKind.ProviderBlocked,
                code ?? "provider_blocked",
                circuitProvider is null
                    ? message
                    : $"The provider circuit for '{circuitProvider}' is open. {message}")));
            return false;
        }

        if (Retryable(kind) && execution.Attempt < MaxStageAttempts)
        {
            var scheduled = await Capture(() => V2(r => r.MarkRetryWaitingAsync(execution, kind, code, message)));
            if (!scheduled)
            {
                await SettlePreArtifactStopAsync(run, "Stopped before the next stage attempt.");
                return false;
            }
            await Capture(() => Task.Delay(RetryDelay));
            return true;
        }

        await Capture(() => V2(r => r.FailStageAttemptAsync(execution, kind,
            code ?? "stage_failed", message)));
        await Capture(() => V2(r => r.FailRunAsync(execution.JobId, execution.RunId, kind,
            code ?? "stage_failed", message)));
        return false;
    }

    private async Task<bool> StopIfRequestedAsync(DownloadRunRequest run, string message, Func<Task>? compensate = null)
    {
        if (!await Capture(() => V2(r => r.IsStopRequestedAsync(run.Request.JobId, run.RunId))))
            return false;
        if (compensate is not null)
            await compensate();
        else
            await Capture(() => V2(r => r.MarkStoppedAsync(run.Request.JobId, run.RunId, message)));
        return true;
    }

    private async Task SettlePreArtifactStopAsync(DownloadRunRequest run, string message)
    {
        if (await Capture(() => V2(r => r.IsStopRequestedAsync(run.Request.JobId, run.RunId))))
            await Capture(() => V2(r => r.MarkStoppedAsync(run.Request.JobId, run.RunId, message)));
    }

    private async Task<DownloadExecutionIdentity> NewExecutionAsync(
        DownloadRunRequest run, DownloadStage stage, int attempt, string? artifactKey = null)
        => new()
        {
            JobId = run.Request.JobId,
            RunId = run.RunId,
            CorrelationId = run.Request.CorrelationId,
            DispatchId = await Capture(Guid.NewGuid),
            Stage = stage,
            ArtifactKey = artifactKey,
            Attempt = attempt
        };

    private static bool Retryable(FailureKind kind) => kind is FailureKind.Unknown or FailureKind.Transient or FailureKind.Timeout;

    private static string Operation(DownloadRunRequest run, DownloadStage stage, int attempt, string? artifactKey = null)
        => $"job/{run.Request.JobId:N}/run/{run.RunId:N}/{stage.ToString().ToLowerInvariant()}" +
           (string.IsNullOrWhiteSpace(artifactKey) ? string.Empty : $"/{artifactKey}") + $"/attempt/{attempt}";

    private static string Tagged(string subject, string? workerTag)
        => string.IsNullOrWhiteSpace(workerTag) ? subject : $"{subject}.{workerTag}";

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => bus.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private static byte[] BuildMetaBytes(DownloadRequested request, Guid mediaGuid, string? title, string hash)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            mediaGuid = mediaGuid.ToString("D"),
            title,
            contentHashXxh128 = hash,
            originalUrl = request.SourceUrl
        }));

    private static string SidecarPath(string primaryStoragePath, string fileName)
    {
        var slash = primaryStoragePath.LastIndexOf('/');
        return slash < 0 ? fileName : $"{primaryStoragePath[..slash]}/{fileName}";
    }

    private static DownloadArtifactSnapshot ToArtifact(
        DownloadRunRequest run, DownloadStage stage, string key, UploadArtifactKind kind, bool required, UploadCompleted upload)
        => new()
        {
            JobId = run.Request.JobId,
            RunId = run.RunId,
            Stage = stage,
            ArtifactKey = key,
            Kind = kind,
            Required = required,
            Status = DownloadArtifactStatus.Stored,
            TempFileRef = upload.TempFileRef,
            StorageKey = upload.StorageKey,
            StoragePath = upload.StoragePath,
            StorageVersion = upload.StorageVersion,
            ContentHashXxh128 = upload.ContentHashXxh128,
            SizeBytes = upload.ContentLengthBytes
        };

    private async Task PublishMetadataSync(Guid mediaGuid)
    {
        try
        {
            await messageBus.PublishAsync(MetadataSyncSubjects.SyncUpsert, new MetadataSyncUpsertMessage { MediaGuid = mediaGuid });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed publishing metadata sync for {MediaGuid}", mediaGuid);
        }
    }

    private async Task QueueAudioRenditionAsync(DownloadRequested request, Guid mediaGuid, int? version, string storageKey)
    {
        try
        {
            var preference = await Playlists(r => r.GetAudioPreferenceForJobAsync(request.JobId));
            if (preference is not { EncodeForPlaylist: true } && !request.EncodeAudioRendition)
                return;
            var targetStorage = preference?.StorageKey ?? storageKey;
            var rendition = await scopeFactory.WithScopedAsync<IAudioRenditionRepository, AudioRenditionDto?>(
                r => r.CreateIfMissingAsync(mediaGuid, targetStorage, version));
            if (rendition is null || rendition.Status == AudioRenditionStatus.Ready)
                return;
            await bus.PublishAsync(BackgroundJobSubjects.AudioRenditionEncodeRequest,
                new AudioRenditionEncodeRequested
                {
                    RenditionId = rendition.RenditionId,
                    MediaGuid = rendition.MediaGuid,
                    SourceVersion = rendition.SourceVersion
                }, rendition.RenditionId.ToString("N"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Download completed but audio-rendition follow-up could not be queued for JobId {JobId} MediaGuid {MediaGuid}.",
                request.JobId,
                mediaGuid);
        }
    }

    private async Task NotifyCompletionAsync(Guid jobId, string sourceUrl)
    {
        try
        {
            await notificationDispatcher.NotifyDownloadOutcomeAsync(
                jobId,
                NotificationEventKeys.DownloadCompleted,
                "FrostStream download completed",
                $"Download completed for {sourceUrl}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Download completed but its notification failed for JobId {JobId}.", jobId);
        }
    }

    private async Task<string?> ResolveWorkerTagAsync(string storageKey)
        => await scopeFactory.WithScopedAsync<DataBridgeDbContext, string?>(db => db.StorageConfigs
            .Where(x => x.Key == storageKey).Select(x => x.WorkerTag).FirstOrDefaultAsync());

    private async Task<DownloadRequested> ResolvePresetAsync(DownloadRequested request)
    {
        if (request.YtDlpOptions is not null || string.IsNullOrWhiteSpace(request.PresetKey))
            return request;
        var preset = await scopeFactory.WithScopedAsync<IOptionPresetsRepository, OptionPresetEntity?>(r => r.GetByKeyAsync(request.PresetKey));
        if (preset is null)
            return request;
        try
        {
            return request with { YtDlpOptions = JsonSerializer.Deserialize<YtDlpOptions>(preset.YtDlpOptionsJson) };
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Could not deserialize preset {PresetKey} for JobId {JobId}", request.PresetKey, request.JobId);
            return request;
        }
    }

    private static readonly JsonSerializerOptions CommentsSidecarJsonOptions =
        JsonSerializerRegistry.CreateDefaultOptions();

    /// <summary>
    /// Commits rich metadata, first merging in the comment thread from the uploaded
    /// <c>comments.json</c> sidecar (comment threads are unbounded, so they travel via storage
    /// instead of NATS payloads). A missing or unreadable sidecar downgrades to a warning —
    /// the archive still holds the full info.json.
    /// </summary>
    private async Task WriteRichMetadataAsync(
        Guid mediaGuid, CapturedMediaMetadata metadata, string storageKey, string? commentsStoragePath)
    {
        if (commentsStoragePath is not null)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var storage = await scope.ServiceProvider.GetRequiredService<Shared.Storage.IBlobStorageProvider>()
                    .GetAsync(storageKey);
                await using var stream = await storage.OpenReadAsync(commentsStoragePath);
                if (stream is not null)
                {
                    var comments = await JsonSerializer.DeserializeAsync<IReadOnlyList<CapturedCommentMetadata>>(
                        stream, CommentsSidecarJsonOptions);
                    if (comments is { Count: > 0 })
                        metadata = metadata with { Comments = comments };
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not load the comments sidecar {StorageKey}:{StoragePath} for MediaGuid {MediaGuid}; committing metadata without comments.",
                    storageKey, commentsStoragePath, mediaGuid);
            }
        }
        await Metadata(r => r.WriteMetadataAsync(mediaGuid, metadata, storageKey));
    }

    private Task V2(Func<IDownloadFlowV2Repository, Task> action) => scopeFactory.WithScopedAsync(action);
    private Task<T> V2<T>(Func<IDownloadFlowV2Repository, Task<T>> action) => scopeFactory.WithScopedAsync(action);
    private Task Jobs(Func<IDownloadJobsRepository, Task> action) => scopeFactory.WithScopedAsync(action);
    private Task<T> Jobs<T>(Func<IDownloadJobsRepository, Task<T>> action) => scopeFactory.WithScopedAsync(action);
    private Task Metadata(Func<IMetadataRepository, Task> action) => scopeFactory.WithScopedAsync(action);
    private Task Playlists(Func<IPlaylistsRepository, Task> action) => scopeFactory.WithScopedAsync(action);
    private Task<T> Playlists<T>(Func<IPlaylistsRepository, Task<T>> action) => scopeFactory.WithScopedAsync(action);

    private sealed record UploadOutcome(
        UploadCompleted? Upload,
        bool Succeeded,
        bool Stopped,
        bool Fatal,
        FailureKind? FailureKind,
        string? FailureCode,
        string? FailureMessage);

    private sealed record RequiredStageOutcome(
        bool Succeeded,
        FailureKind FailureKind,
        string FailureCode,
        string FailureMessage)
    {
        public static RequiredStageOutcome Success { get; } = new(
            true, FailureKind.Unknown, string.Empty, string.Empty);

        public static RequiredStageOutcome Failed(
            FailureKind kind,
            string code,
            string message) => new(false, kind, code, message);
    }
}
