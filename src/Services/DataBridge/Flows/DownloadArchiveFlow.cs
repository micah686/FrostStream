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
    IMessageBus bus,
    IServiceScopeFactory scopeFactory,
    IClock clock
) : Flow<DownloadRequested>
{
    public override async Task Run(DownloadRequested request)
    {
        var jobId = request.JobId;
        var jobInstance = jobId.ToString("N");

        await Capture(() => Update(jobId, DownloadJobState.Queued));

        // STEP 1: metadata ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.MetadataPending));
        await Capture(() => RepoCall(r => r.IncrementMetadataAttemptAsync(jobId, 1)));

        var fetchMetadata = new FetchMetadataCommand
        {
            JobId = jobId,
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            MessageId = await Capture(Guid.NewGuid),
            OperationKey = $"job/{jobInstance}/metadata/attempt/1",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = request.SourceUrl
        };
        await Capture(() => bus.PublishAsync(DownloadSubjects.FetchMetadataCommand, fetchMetadata));

        var metadataResult = await Messages.FirstOfTypes<MetadataFetched, MetadataFetchFailed>();
        if (metadataResult.HasSecond)
        {
            await Capture(() => Fail(jobId, metadataResult.Second, DownloadJobState.FailedTransient));
            return;
        }
        var metadata = metadataResult.First;

        // STEP 2: download ----------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.DownloadPending));
        await Capture(() => RepoCall(r => r.IncrementDownloadAttemptAsync(jobId, 1)));

        var downloadVideo = new DownloadVideoCommand
        {
            JobId = jobId,
            CorrelationId = request.CorrelationId,
            CausationId = metadata.MessageId,
            MessageId = await Capture(Guid.NewGuid),
            OperationKey = $"job/{jobInstance}/download/attempt/1",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            SourceUrl = request.SourceUrl,
            ArchiveKey = metadata.ArchiveKey
        };
        await Capture(() => bus.PublishAsync(DownloadSubjects.DownloadVideoCommand, downloadVideo));

        var downloadResult = await Messages.FirstOfTypes<DownloadCompleted, DownloadFailed>();
        if (downloadResult.HasSecond)
        {
            // Compensation: if temp file exists, dispatch cleanup before failing.
            if (!string.IsNullOrEmpty(downloadResult.Second.TempFileRef))
            {
                await Capture(() => DispatchTempFileCleanup(request, downloadResult.Second.TempFileRef!, jobInstance, attempt: 1));
            }
            await Capture(() => Fail(jobId, downloadResult.Second, DownloadJobState.FailedTransient));
            return;
        }
        var downloaded = downloadResult.First;

        // STEP 3: upload ------------------------------------------------------
        await Capture(() => Update(jobId, DownloadJobState.UploadPending));
        await Capture(() => RepoCall(r => r.IncrementUploadAttemptAsync(jobId, 1)));

        var uploadObject = new UploadObjectCommand
        {
            JobId = jobId,
            CorrelationId = request.CorrelationId,
            CausationId = downloaded.MessageId,
            MessageId = await Capture(Guid.NewGuid),
            OperationKey = $"job/{jobInstance}/upload/attempt/1",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            TempFileRef = downloaded.TempFileRef,
            StorageKey = request.StorageKey ?? "default",
            ArchiveKey = metadata.ArchiveKey
        };
        await Capture(() => bus.PublishAsync(DownloadSubjects.UploadObjectCommand, uploadObject));

        var uploadResult = await Messages.FirstOfTypes<UploadCompleted, UploadFailed>();
        if (uploadResult.HasSecond)
        {
            // Compensation: clean up temp file if one is still around.
            await Capture(() => DispatchTempFileCleanup(request, downloaded.TempFileRef, jobInstance, attempt: 1));
            await Capture(() => Fail(jobId, uploadResult.Second, DownloadJobState.FailedTransient));
            return;
        }
        var uploaded = uploadResult.First;

        // STEP 4: authoritative DB commit ------------------------------------
        // (Ingress already wrote storage coords + State=Uploaded; we only flip to Completed here.
        //  When an "archive" table is added, do that commit transactionally before MarkCompleted.)
        await Capture(() => Update(jobId, DownloadJobState.CommitPending));
        await Capture(() => Update(jobId, DownloadJobState.Completed));

        // STEP 5: cleanup -----------------------------------------------------
        var cleanup = new DeleteTempFileCommand
        {
            JobId = jobId,
            CorrelationId = request.CorrelationId,
            CausationId = uploaded.MessageId,
            MessageId = await Capture(Guid.NewGuid),
            OperationKey = $"job/{jobInstance}/cleanup-temp/attempt/1",
            OccurredAt = clock.GetCurrentInstant(),
            Attempt = 1,
            TempFileRef = uploaded.TempFileRef
        };
        await Capture(() => bus.PublishAsync(DownloadSubjects.DeleteTempFileCommand, cleanup));

        // Best-effort: don't fail the job if cleanup itself fails — leaked temp files become
        // operator debt, not a job failure. Swap to Messages.FirstOfTypes<...> if you want
        // to surface TempFileDeleteFailed into the failure log.
        await Message<TempFileDeleted>();
    }

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
        await bus.PublishAsync(DownloadSubjects.DeleteTempFileCommand, cleanup);
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
        var resolvedTerminal = kind == FailureKind.Permanent
            ? DownloadJobState.FailedPermanent
            : terminalState;

        await RepoCall(r => r.RecordTerminalFailureAsync(jobId, kind, code, message, resolvedTerminal, lastPayloadJson: null));
    }

    private async Task RepoCall(Func<IDownloadJobsRepository, Task> action)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
        await action(jobs);
    }
}
