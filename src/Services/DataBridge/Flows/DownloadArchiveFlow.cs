using Cleipnir.Flows;
using Cleipnir.ResilientFunctions.Reactive.Extensions;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Flows;

[GenerateFlows]
public class DownloadArchiveFlow(
    IJetStreamPublisher bus,
    IServiceScopeFactory scopeFactory,
    IClock clock
) : Flow<DownloadRequested>
{
    private const int MaxAttempts = 3;

    public override async Task Run(DownloadRequested request)
    {
        var jobId = request.JobId;
        var jobInstance = jobId.ToString("N");

        await Capture(() => Update(jobId, DownloadJobState.Queued));

        // STEP 1: metadata ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.MetadataPending));
        var metadata = await RunMetadataStep(request, jobInstance);
        if (metadata is null) return;

        var sourceVersion = await Capture(() => RepoCall(r =>
            r.RegisterSourceVersionAsync(metadata, request.ForceDownload)));
        if (sourceVersion.AlreadyDownloaded)
        {
            await Capture(() => RepoCall(r => r.MarkAlreadyDownloadedAsync(
                jobId,
                sourceVersion.LatestContentHashXxh128)));
            return;
        }

        // STEP 2: download ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.DownloadPending));
        var downloaded = await RunDownloadStep(request, metadata, jobInstance);
        if (downloaded is null) return;

        // STEP 3: upload ------------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.UploadPending));
        var uploaded = await RunUploadStep(request, metadata, downloaded, jobInstance);
        if (uploaded is null) return;

        // STEP 4: authoritative DB commit ------------------------------------
        // Ingress already wrote storage coords + State=Uploaded; we only flip to Completed
        // here. When an "archive" table is added, do that commit transactionally before
        // marking Completed. If ANY part of this commit fails after the upload landed,
        // we MUST tear down the storage object so DB and storage don't drift.
        try
        {
            await Capture(() => Update(jobId, DownloadJobState.CommitPending));
            await Capture(() => Update(jobId, DownloadJobState.Completed));
        }
        catch (Exception commitEx)
        {
            await Capture(() => DispatchUploadedObjectDeletion(request, uploaded, jobInstance, attempt: 1));
            await Capture(() => DispatchTempFileCleanup(request, uploaded.TempFileRef, jobInstance, attempt: 1));
            await Capture(() => RepoCall(r => r.RecordTerminalFailureAsync(
                jobId,
                FailureKind.Permanent,
                code: "commit_failed",
                message: commitEx.Message,
                terminalState: DownloadJobState.FailedPermanent,
                lastPayloadJson: null)));
            return;
        }

        // STEP 5: cleanup -----------------------------------------------------
        // Best-effort — leaked temp files are operator debt, not job failure. The flow does
        // wait for TempFileDeleted so the job's history captures the cleanup outcome.
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
            await Capture(() => Publish(DownloadSubjects.FetchMetadataCommand, cmd));

            var result = await Messages.FirstOfTypes<MetadataFetched, MetadataFetchFailed>();
            if (result.HasFirst) return result.First;

            var failure = result.Second;
            // No prior side effects to compensate for — metadata is read-only on Worker.
            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                await Capture(() => Fail(request.JobId, failure, terminal));
                return null;
            }
        }
        return null;
    }

    private async Task<DownloadCompleted?> RunDownloadStep(DownloadRequested request, MetadataFetched metadata, string jobInstance)
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
                CausationId = metadata.MessageId,
                MessageId = msgId,
                OperationKey = op,
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = attempt,
                SourceUrl = request.SourceUrl,
                StorageKey = request.StorageKey ?? "default",
                ArchiveKey = metadata.ArchiveKey
            };
            await Capture(() => Publish(DownloadSubjects.DownloadVideoCommand, cmd));

            var result = await Messages.FirstOfTypes<DownloadCompleted, DownloadFailed>();
            if (result.HasFirst) return result.First;

            var failure = result.Second;
            // Compensation: any partial temp file from this attempt must be cleaned up
            // before retry or termination, regardless of whether we're going to retry.
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

    private async Task<UploadCompleted?> RunUploadStep(DownloadRequested request, MetadataFetched metadata, DownloadCompleted downloaded, string jobInstance)
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
                StorageKey = request.StorageKey ?? "default",
                ArchiveKey = metadata.ArchiveKey
            };
            await Capture(() => Publish(DownloadSubjects.UploadObjectCommand, cmd));

            var result = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
            if (result.HasFirst) return result.First;

            var failure = result.Second;
            if (TerminalFailureForStep(failure, attempt) is { } terminal)
            {
                // Final: clean up the temp file we still own and mark failed.
                await Capture(() => DispatchTempFileCleanup(request, downloaded.TempFileRef, jobInstance, attempt));
                await Capture(() => Fail(request.JobId, failure, terminal));
                return null;
            }
            // Retry: re-uploading the same temp file is the natural retry behavior.
        }
        return null;
    }

    /// <summary>
    /// Returns the terminal state to record IF this attempt should be the last,
    /// or <c>null</c> when the loop should retry. Permanent failures terminate immediately;
    /// transient/timeout/unknown failures retry until <see cref="MaxAttempts"/>.
    /// </summary>
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
            ObjectKey = uploaded.ObjectKey,
            StorageVersion = uploaded.StorageVersion
        };
        await Publish(DownloadSubjects.DeleteUploadedObjectCommand, deletion);
    }

    private Task Update(Guid jobId, DownloadJobState state)
        => RepoCall(r => r.UpdateStateAsync(jobId, state));

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
        await RepoCall(r => r.RecordTerminalFailureAsync(jobId, kind, code, message, terminalState, lastPayloadJson: null));
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
