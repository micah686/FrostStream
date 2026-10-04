using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using System.Diagnostics;
using DataBridge.Persistence;
using DataBridge.Persistence.Sqlite;
using System.Text.Json;
using FrostStream.ApplicationContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Database;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;
using WebAPI.Features.Downloads;

namespace IntegrationTests.Lite;

[NotInParallel("LiteRuntimeQuartz")]
public sealed class LiteRuntimeTests
{
    [Test]
    public async Task Download_Import_Processing_Playback_Search_Scheduling_And_Progress_Through_One_Host()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var fixture = new LiteRuntimeFixture();
        await fixture.StartAsync(token);
        (await fixture.ReadAsync<JsonElement>("/api/auth/me", token)).ToString().ShouldContain("Admin");
        (await fixture.ReadAsync<JsonElement>("/api/system/capabilities", token)).GetProperty("deploymentMode").GetString().ShouldBe("Lite");

        // Multiple HTTP writers exercise the same serialized SQLite mutations as production.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => fixture.SendAsync(HttpMethod.Post, "/api/storage/local/create",
            new { key = $"concurrent-{i}", protocol = "Local", path = Path.Combine(fixture.DirectoryPath, $"store-{i}") }, token)));
        (await fixture.DatabaseAsync(db => db.StorageConfigs.CountAsync(x => x.Key.StartsWith("concurrent-"), token))).ShouldBe(8);

        using (var newFeature = await fixture.Client.GetAsync("/api/e2e/new-feature", token))
        {
            newFeature.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await newFeature.Content.ReadAsStringAsync(token)).ShouldContain("Admin");
        }
        var queueEvents = fixture.App.Services.GetRequiredService<DownloadQueueHub>().SubscribeToQueue();
        try
        {
            var jobId = await fixture.DownloadAsync("normal", token);
            await WaitCompletedAsync(fixture, jobId, token);
            var job = await fixture.ReadAsync<DownloadQueueJobDto>($"/api/downloads/queue/{jobId}", token);
            job.Status.ShouldBe(DownloadJobStatus.Completed);
            var history = await fixture.ReadAsync<List<DownloadQueueHistoryEntryDto>>($"/api/downloads/queue/{jobId}/history", token);
            history.Select(x => x.EventName).ShouldContain(nameof(DownloadCompleted));
            var sawProgress = false;
            while (queueEvents.Reader.TryRead(out var frame))
                sawProgress |= frame is QueueStreamEvent.Progress progress && progress.JobId == jobId;
            sawProgress.ShouldBeTrue();
            var media = await fixture.ReadAsync<DownloadQueueMediaResponse>($"/api/downloads/queue/{jobId}/media", token);
            var guid = media.MediaGuid!.Value;

            // A second delivery with a different transport id exercises application-level deduplication.
            var persisted = await fixture.DatabaseAsync(db => db.DownloadJobHistory.Where(x => x.JobId == jobId && x.EventName == nameof(DownloadRequested)).SingleAsync(token));
            var requested = new DownloadRequested
            {
                JobId = jobId, CorrelationId = job.CorrelationId, MessageId = persisted.MessageId,
                OperationKey = persisted.OperationKey, OccurredAt = job.CreatedAt,
                SourceUrl = job.SourceUrl, StorageKey = "e2e-storage"
            };
            await fixture.App.Services.GetRequiredService<IDurableJobPublisher>().PublishAsync(DownloadSubjects.DownloadRequested, requested, "e2e-redelivery", cancellationToken: token);
            await LiteRuntimeFixture.EventuallyAsync(() =>
            {
                var connections = fixture.App.Services.GetRequiredService<SqliteConnectionFactory>();
                using var connection = connections.OpenConnection(token);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM messaging_inbox i JOIN messaging_messages m ON m.seq=i.seq WHERE m.message_id='e2e-redelivery' AND i.acknowledged=1";
                return Task.FromResult(Convert.ToInt64(command.ExecuteScalar()) == 1);
            }, token);
            (await fixture.DatabaseAsync(db => db.DownloadJobRuns.CountAsync(x => x.JobId == jobId, token))).ShouldBe(1);

            using var playback = await fixture.Client.GetAsync($"/api/media/watch/{guid}", token);
            playback.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await playback.Content.ReadAsByteArrayAsync(token)).ShouldBe(await File.ReadAllBytesAsync(fixture.Video, token));
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/media/watch/{guid}");
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 15);
            using var range = await fixture.Client.SendAsync(rangeRequest, token);
            range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
            (await range.Content.ReadAsByteArrayAsync(token)).Length.ShouldBe(16);

            using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var progressResponseTask = fixture.Client.GetAsync($"/api/media/renditions/progress/stream?mediaGuid={guid}", HttpCompletionOption.ResponseHeadersRead, progressStop.Token);
            using var prepare = await fixture.Client.GetAsync($"/api/media/stream/{guid}/index.m3u8", token);
            prepare.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            using var progressResponse = await progressResponseTask.WaitAsync(token);
            progressResponse.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
            using var progressReader = new StreamReader(await progressResponse.Content.ReadAsStreamAsync(token));
            (await progressReader.ReadLineAsync(token)).ShouldBe("event: progress");
            (await progressReader.ReadLineAsync(token))!.ShouldContain(guid.ToString());
            await LiteRuntimeFixture.EventuallyAsync(async () =>
            {
                var rendition = await fixture.DatabaseAsync(db => db.StreamRenditions.SingleAsync(x => x.MediaGuid == guid, token));
                rendition.Status.ShouldNotBe(StreamRenditionStatus.Failed, rendition.ErrorMessage);
                return rendition.Status == StreamRenditionStatus.Ready;
            }, token);
            progressReader.Dispose();
            progressResponse.Dispose();
            progressStop.Cancel();
            using var manifest = await fixture.Client.GetAsync($"/api/media/stream/{guid}/index.m3u8", token);
            var playlist = await manifest.Content.ReadAsStringAsync(token);
            manifest.StatusCode.ShouldBe(HttpStatusCode.OK);
            playlist.ShouldContain("#EXTM3U");
            var segmentUrl = playlist.Split('\n').First(line => !line.StartsWith('#') && !string.IsNullOrWhiteSpace(line)).Trim();
            using var segment = await fixture.Client.GetAsync(segmentUrl, token);
            segment.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await segment.Content.ReadAsByteArrayAsync(token)).Length.ShouldBeGreaterThan(0);

            await LiteRuntimeFixture.EventuallyAsync(async () =>
                (await fixture.ReadAsync<JsonElement>("/api/search?q=deterministic&scope=metadata", token)).GetProperty("items").EnumerateArray()
                    .Any(hit => hit.GetProperty("media").GetProperty("mediaGuid").GetGuid() == guid), token);

            // Scan, probe, user edit, commit, hash, upload, and finalize a real incoming file.
            File.Copy(fixture.Video, Path.Combine(fixture.Incoming, "import.mp4"));
            var created = await fixture.SendAsync(HttpMethod.Post, "/api/global/imports/sessions", new { storageKey = "e2e-storage" }, token);
            var sessionId = created.GetProperty("sessionId").GetGuid();
            await LiteRuntimeFixture.EventuallyAsync(async () =>
            {
                var state = await fixture.ReadAsync<ImportSessionDto>($"/api/global/imports/sessions/{sessionId}", token);
                state.Status.ShouldNotBe(ImportSessionStatus.ScanFailed, state.ErrorMessage);
                return state.Status == ImportSessionStatus.Reviewing && state.TotalItems == 1;
            }, token);
            var items = await fixture.ReadAsync<ImportSessionItemsListResponse>($"/api/global/imports/sessions/{sessionId}/items", token);
            var item = items.Items.ShouldHaveSingleItem();
            await fixture.SendAsync(HttpMethod.Patch, $"/api/global/imports/sessions/{sessionId}/items/{item.ItemId}",
                new { title = "Lite imported video", provider = "generic", sourceMediaId = "imported", sourceUrl = "https://fixture.example.test/imported" }, token);
            await fixture.SendAsync(HttpMethod.Post, $"/api/global/imports/sessions/{sessionId}/items/bulk",
                new { action = "Include", itemIds = new[] { item.ItemId } }, token);
            await LiteRuntimeFixture.EventuallyAsync(async () =>
                (await fixture.ReadAsync<ImportSessionDto>($"/api/global/imports/sessions/{sessionId}", token)).ProbedItems == 1, token);
            await fixture.SendAsync(HttpMethod.Post, $"/api/global/imports/sessions/{sessionId}/commit", new { }, token);
            await LiteRuntimeFixture.EventuallyAsync(async () =>
            {
                var state = await fixture.ReadAsync<ImportSessionDto>($"/api/global/imports/sessions/{sessionId}", token);
                state.Status.ShouldNotBe(ImportSessionStatus.CompletedWithFailures, state.ErrorMessage);
                return state.Status == ImportSessionStatus.Completed && state.ImportedItems + state.AlreadyImportedItems == 1;
            }, token);

            await fixture.SendAsync(HttpMethod.Post, "/api/global/schedules", new { key = "e2e-maintenance", taskType = "db-maintenance", intervalSeconds = 3600, enabled = true }, token);
            await LiteRuntimeFixture.EventuallyAsync(() => fixture.DatabaseAsync(db => db.ScheduledTasks.AnyAsync(x => x.Key == "e2e-maintenance" && x.LastRunStatus == ScheduleRunStatus.Completed, token)), token);
            await fixture.RestartAsync(token);
            (await fixture.ReadAsync<DownloadQueueJobDto>($"/api/downloads/queue/{jobId}", token)).Status.ShouldBe(DownloadJobStatus.Completed);
            (await fixture.ReadAsync<JsonElement>("/api/auth/me", token)).ToString().ShouldContain("Admin");
            var scheduler = await fixture.App.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(token);
            (await scheduler.CheckExists(new JobKey("e2e-maintenance", "scheduled-tasks"), token)).ShouldBeTrue();
            fixture.RemovedInfrastructureConnections.ShouldBe(0);
        }
        finally { fixture.App.Services.GetRequiredService<DownloadQueueHub>().Unsubscribe(queueEvents.Id); }
    }

    [Test]
    public async Task Stop_Cancels_Worker_And_Restart_Fences_Interrupted_Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var fixture = new LiteRuntimeFixture();
        await fixture.StartAsync(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeDownload = async (_, cancellation) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        var jobId = await fixture.DownloadAsync("cancel", token);
        await entered.Task.WaitAsync(token);
        await fixture.SendAsync(HttpMethod.Post, $"/api/downloads/queue/{jobId}/stop", new { reason = "integration cancellation" }, token);
        await cancelled.Task.WaitAsync(token);
        await LiteRuntimeFixture.EventuallyAsync(() => fixture.DatabaseAsync(db => db.DownloadJobs.AnyAsync(x => x.JobId == jobId && x.Status == DownloadJobStatus.Stopped, token)), token);
        var runId = await fixture.DatabaseAsync(db => db.DownloadJobs.Where(x => x.JobId == jobId).Select(x => x.CurrentRunId).SingleAsync(token));
        fixture.BeforeDownload = null;
        await fixture.RestartAsync(token);
        (await fixture.ReadAsync<DownloadQueueJobDto>($"/api/downloads/queue/{jobId}", token)).Status.ShouldBe(DownloadJobStatus.Stopped);
        (await fixture.DatabaseAsync(db => db.DownloadJobRuns.CountAsync(x => x.JobId == jobId, token))).ShouldBe(1);
        (await fixture.DatabaseAsync(db => db.DownloadWorkerLeases.CountAsync(x => x.RunId == runId && x.Status == DownloadWorkerLeaseStatus.Active, token))).ShouldBe(0);
        await fixture.SendAsync(HttpMethod.Post, $"/api/downloads/queue/{jobId}/start", new { }, token);
        await WaitCompletedAsync(fixture, jobId, token);
        (await fixture.DatabaseAsync(db => db.DownloadJobRuns.CountAsync(x => x.JobId == jobId, token))).ShouldBe(2);
    }

    [Test]
    public async Task Killed_Process_Reconciles_Active_Work_And_Rejects_Stale_Durable_Redelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var fixture = new LiteRuntimeFixture();
        await fixture.StartAsync(token);
        await fixture.StopHostAsync(token);
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (var argument in new[] { typeof(LiteRuntimeTests).Assembly.Location, "--lite-crash-host", fixture.DirectoryPath,
                     fixture.TypesenseUrl, fixture.ForbiddenPort.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            child.StartInfo.ArgumentList.Add(argument);
        child.Start();
        var output = child.StandardOutput.ReadToEndAsync(token);
        var errors = child.StandardError.ReadToEndAsync(token);
        try
        {
            var addressFile = Path.Combine(fixture.DirectoryPath, "child-address");
            await LiteRuntimeFixture.EventuallyAsync(async () =>
            {
                child.HasExited.ShouldBeFalse(child.HasExited ? await errors : "Child exited unexpectedly");
                return File.Exists(addressFile);
            }, token);
            using var client = new HttpClient { BaseAddress = new Uri(await File.ReadAllTextAsync(addressFile, token)) };
            using var response = await client.PostAsJsonAsync("/api/downloads/video",
                new { sourceUrl = "https://fixture.example.test/crash", storageKey = "e2e-storage" }, LiteRuntimeFixture.Json, token);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var jobId = (await response.Content.ReadFromJsonAsync<JsonElement>(LiteRuntimeFixture.Json, token)).GetProperty("jobId").GetGuid();
            fixture.Jobs.Add(jobId);
            await LiteRuntimeFixture.EventuallyAsync(() => Task.FromResult(File.Exists(Path.Combine(fixture.DirectoryPath, "acquisition-started"))), token);
            // Deliberately kill the process without graceful host shutdown.
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(token);
            await Task.WhenAll(output, errors);
            var factory = new SqliteConnectionFactory(new(PersistenceProvider.Sqlite, Path.Combine(fixture.DirectoryPath, "application.db"), 5));
            using (var connection = factory.OpenConnection(token))
            using (var command = connection.CreateCommand())
            {
                // Advance the inbox lease's clock rather than spending two minutes waiting for TTL.
                command.CommandText = "UPDATE messaging_inbox SET available=0 WHERE acknowledged=0";
                command.ExecuteNonQuery().ShouldBeGreaterThan(0);
            }
            await fixture.ResumeHostAsync(token);
            var job = await fixture.ReadAsync<DownloadQueueJobDto>($"/api/downloads/queue/{jobId}", token);
            job.Status.ShouldBe(DownloadJobStatus.Failed);
            job.FailureKind.ShouldBe(FailureKind.Interrupted);
            (await fixture.DatabaseAsync(db => db.DownloadWorkerLeases.CountAsync(x => x.JobId == jobId && x.Status == DownloadWorkerLeaseStatus.Active, token))).ShouldBe(0);
            await LiteRuntimeFixture.EventuallyAsync(() =>
            {
                using var connection = factory.OpenConnection(token);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM messaging_inbox WHERE acknowledged=0";
                return Task.FromResult(Convert.ToInt64(command.ExecuteScalar()) == 0);
            }, token);
            (await fixture.DatabaseAsync(db => db.DownloadJobRuns.CountAsync(x => x.JobId == jobId, token))).ShouldBe(1);
            await fixture.Downloader.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!);
            fixture.RemovedInfrastructureConnections.ShouldBe(0);
            await fixture.SendAsync(HttpMethod.Post, $"/api/downloads/queue/{jobId}/start", new { }, token);
            await WaitCompletedAsync(fixture, jobId, token);
            (await fixture.DatabaseAsync(db => db.DownloadJobRuns.CountAsync(x => x.JobId == jobId, token))).ShouldBe(2);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
        }
    }

    internal static Task WaitCompletedAsync(LiteRuntimeFixture fixture, Guid jobId, CancellationToken token)
        => LiteRuntimeFixture.EventuallyAsync(async () =>
        {
            var job = await fixture.DatabaseAsync(db => db.DownloadJobs.SingleOrDefaultAsync(x => x.JobId == jobId, token));
            job?.Status.ShouldNotBe(DownloadJobStatus.Failed, job?.FailureMessage);
            return job?.Status == DownloadJobStatus.Completed;
        }, token);
}
