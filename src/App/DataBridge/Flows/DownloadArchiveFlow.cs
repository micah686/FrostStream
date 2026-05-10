using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;
using Shared.Metadata;

namespace DataBridge.Flows;

[GenerateFlows]
public class DownloadArchiveFlow(
    IJetStreamPublisher bus,
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<DownloadArchiveFlow> logger
) : Flow<DownloadRequested>
{
    private const int MaxAttempts = 3;

    public override async Task Run(DownloadRequested request)
    {
        var jobId = request.JobId;
        var jobInstance = jobId.ToString("N");
        var storageKey = request.StorageKey ?? "default";

        logger.LogInformation(
            "Download flow started for JobId {JobId} CorrelationId {CorrelationId} URL {SourceUrl} StorageKey {StorageKey} ForceDownload {ForceDownload}",
            jobId,
            request.CorrelationId,
            request.SourceUrl,
            storageKey,
            request.ForceDownload);

        await Capture(() => Update(jobId, DownloadJobState.Queued));

        // STEP 1: metadata ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.MetadataPending));
        var metadata = await RunMetadataStep(request, jobInstance);
        if (metadata is null) return;

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

        // STEP 3: download ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.DownloadPending));
        var downloaded = await RunDownloadStep(request, jobInstance);
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
            await Capture(() => DispatchTempFileCleanup(request, downloaded.TempFileRef, jobInstance, attempt: 1));
            await Message<TempFileDeleted>();
            if (metadata.RichMetadata is { } existingRichMeta)
                await RunMetadataWriteStep(jobId, reservation.MediaGuid, reservation.IsNewMediaGuid, metadata.Provider, metadata.SourceMediaId, existingRichMeta);
            await Capture(() => PlaylistRepoCall(r => r.TryLinkMediaGuidAsync(jobId, reservation.MediaGuid)));
            await Capture(() => RepoCall(r => r.MarkAlreadyDownloadedAsync(jobId, reservation.MediaGuid)));
            return;
        }

        // STEP 5: upload to the reserved path --------------------------------
        await Capture(() => Update(jobId, DownloadJobState.UploadPending));
        var uploaded = await RunUploadStep(request, downloaded, reservation, storageKey, jobInstance);
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
            await Capture(() => Update(jobId, DownloadJobState.Completed));
        }
        catch (Exception commitEx)
        {
            logger.LogError(commitEx,
                "Download flow commit failed for JobId {JobId} MediaGuid {MediaGuid} StoragePath {StoragePath}; starting compensation.",
                jobId,
                reservation.MediaGuid,
                uploaded.StoragePath);
            await Capture(() => DispatchUploadedObjectDeletion(request, uploaded, jobInstance, attempt: 1));
            await Capture(() => DispatchTempFileCleanup(request, uploaded.TempFileRef, jobInstance, attempt: 1));
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
            return;
        }

        // STEP 6b: write rich metadata ----------------------------------------
        if (metadata.RichMetadata is { } richMeta)
            await RunMetadataWriteStep(jobId, reservation.MediaGuid, reservation.IsNewMediaGuid, metadata.Provider, metadata.SourceMediaId, richMeta);

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
            TempFileRef = uploaded.TempFileRef
        };
        await Capture(() => Publish(DownloadSubjects.DeleteTempFileCommand, cleanup));
        await Message<TempFileDeleted>();

        logger.LogInformation(
            "Download flow completed for JobId {JobId} MediaGuid {MediaGuid} StoragePath {StoragePath}",
            jobId,
            reservation.MediaGuid,
            uploaded.StoragePath);
    }

    private async Task<MetadataFetched?> RunMetadataStep(DownloadRequested request, string jobInstance)
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
                SourceUrl = request.SourceUrl
            };
            logger.LogInformation(
                "Download flow dispatching metadata fetch for JobId {JobId} Attempt {Attempt} OperationKey {OperationKey}",
                request.JobId,
                attempt,
                op);
            await Capture(() => Publish(DownloadSubjects.FetchMetadataCommand, cmd));

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
                "Download flow metadata fetch failed for JobId {JobId} Attempt {Attempt} FailureKind {FailureKind} ErrorCode {ErrorCode} ErrorMessage {ErrorMessage}",
                request.JobId,
                attempt,
                failure.FailureKind,
                failure.ErrorCode,
                failure.ErrorMessage);
            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                await Capture(() => Fail(request.JobId, failure, terminal));
                return null;
            }
        }
        return null;
    }

    private async Task<DownloadCompleted?> RunDownloadStep(DownloadRequested request, string jobInstance)
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
                SourceUrl = request.SourceUrl
            };
            logger.LogInformation(
                "Download flow dispatching video download for JobId {JobId} Attempt {Attempt} OperationKey {OperationKey}",
                request.JobId,
                attempt,
                op);
            await Capture(() => Publish(DownloadSubjects.DownloadVideoCommand, cmd));

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
                return result.First;
            }

            var failure = result.Second;
            logger.LogWarning(
                "Download flow video download failed for JobId {JobId} Attempt {Attempt} FailureKind {FailureKind} ErrorCode {ErrorCode} ErrorMessage {ErrorMessage} TempFileRef {TempFileRef}",
                request.JobId,
                attempt,
                failure.FailureKind,
                failure.ErrorCode,
                failure.ErrorMessage,
                failure.TempFileRef);
            if (!string.IsNullOrEmpty(failure.TempFileRef))
            {
                await Capture(() => DispatchTempFileCleanup(request, failure.TempFileRef!, jobInstance, attempt));
            }

            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                await Capture(() => Fail(request.JobId, failure, terminal));
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
        string jobInstance)
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
                StorageKey = storageKey,
                StoragePath = reservation.StoragePath,
                ContentHashXxh128 = downloaded.ContentHashXxh128
            };
            logger.LogInformation(
                "Download flow dispatching upload for JobId {JobId} Attempt {Attempt} StorageKey {StorageKey} StoragePath {StoragePath} OperationKey {OperationKey}",
                request.JobId,
                attempt,
                storageKey,
                reservation.StoragePath,
                op);
            await Capture(() => Publish(DownloadSubjects.UploadObjectCommand, cmd));

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
                await Capture(() => DispatchTempFileCleanup(request, downloaded.TempFileRef, jobInstance, attempt));
                await Capture(() => Fail(request.JobId, failure, terminal));
                return null;
            }
        }
        return null;
    }

    private static DownloadJobState? TerminalFailureForStep<TFailure>(TFailure failure, int attempt)
        where TFailure : IFlowMessage
    {
        var kind = failure switch
        {
            MetadataFetchFailed m => m.FailureKind,
            DownloadFailed d => d.FailureKind,
            UploadFailed u => u.FailureKind,
            _ => FailureKind.Unknown
        };

        if (kind is FailureKind.Permanent or FailureKind.Cancelled)
            return DownloadJobState.FailedPermanent;
        if (attempt >= MaxAttempts)
            return DownloadJobState.FailedTransient;
        return null;
    }

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => bus.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private async Task DispatchTempFileCleanup(DownloadRequested request, string tempFileRef, string jobInstance, int attempt)
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
            TempFileRef = tempFileRef
        };
        await Publish(DownloadSubjects.DeleteTempFileCommand, cleanup);
    }

    private async Task DispatchUploadedObjectDeletion(DownloadRequested request, UploadCompleted uploaded, string jobInstance, int attempt)
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
            StorageKey = uploaded.StorageKey,
            StoragePath = uploaded.StoragePath,
            StorageVersion = uploaded.StorageVersion
        };
        await Publish(DownloadSubjects.DeleteUploadedObjectCommand, deletion);
    }

    private async Task Update(Guid jobId, DownloadJobState state)
    {
        logger.LogInformation("Download flow state update for JobId {JobId}: {State}", jobId, state);
        await RepoCall(r => r.UpdateStateAsync(jobId, state));
    }

    private async Task Fail<TFailure>(Guid jobId, TFailure failure, DownloadJobState terminalState)
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
            jobId,
            terminalState,
            kind,
            code,
            message);
        await RepoCall(r => r.RecordTerminalFailureAsync(jobId, kind, code, message, terminalState, lastPayloadJson: null));
    }

    private async Task RunMetadataWriteStep(
        Guid jobId,
        Guid mediaGuid,
        bool isNewMediaGuid,
        string? provider,
        string? sourceMediaId,
        CapturedMediaMetadata richMeta)
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
                await Capture(() => MetaRepoCall(r => r.WriteMetadataAsync(mediaGuid, richMeta)));
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
                return;
            }
        }
    }

    private async Task MetaRepoCall(Func<IMetadataRepository, Task> action)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        await action(repo);
    }

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
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();
        await action(repo);
    }

    private async Task<T> PlaylistRepoCall<T>(Func<IPlaylistsRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlaylistsRepository>();
        return await action(repo);
    }

    private async Task RepoCall(Func<IDownloadJobsRepository, Task> action)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
        await action(jobs);
    }

    private async Task<T> RepoCall<T>(Func<IDownloadJobsRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
        return await action(jobs);
    }
}
