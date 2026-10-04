using System.Net;
using System.Net.Http.Json;
using DataBridge.Data;
using DataBridge.Messaging;
using DataBridge.Search;
using Lite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Quartz;
using Shared.Auth;
using Shared.Backups;
using Typesense;
using WebAPI;
using YtDlpSharpLib.Provisioning;
using Shouldly;

namespace UnitTests.Deployment;

public sealed class LiteHostStartupTests
{
    private static WebApplicationBuilder Builder(string directory)
    {
        Quartz.Logging.LogContext.SetCurrentLogProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Configuration["Persistence:Sqlite:Path"] = Path.Combine(directory, "test.db");
        builder.Configuration["Backup:Directory"] = Path.Combine(directory, "backups");
        builder.Configuration["Worker:IncomingRoot"] = Path.Combine(directory, "incoming");
        builder.ConfigureLiteHost();
        builder.AddServiceDefaults();
        builder.AddLiteModules();
        builder.Services.Replace(ServiceDescriptor.Singleton(Substitute.For<ITypesenseClient>()));
        var index = Substitute.For<ITypesenseIndexService>();
        index.GetDocumentCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);
        index.MediaCollectionHasFieldAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        builder.Services.Replace(ServiceDescriptor.Singleton(index));
        return builder;
    }

    private static void ReplaceDownloader(WebApplicationBuilder builder, Task<BinaryDownloadResult> completion,
        TaskCompletionSource? called = null)
    {
        var downloader = Substitute.For<IYtDlpBinaryDownloader>();
        downloader.DownloadAllAsync(Arg.Any<BinaryDownloadOptions?>(), Arg.Any<IProgress<BinaryDownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { called?.TrySetResult(); return completion; });
        builder.Services.Replace(ServiceDescriptor.Singleton(downloader));
    }

    [Test]
    public async Task Lite_Starts_All_Modules_Without_Removed_Infrastructure_And_Only_Serves_After_Initialization()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var builder = Builder(dir);
            var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource<BinaryDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReplaceDownloader(builder, ready.Task, called);
            await using var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.MapWebAPIModule();
            app.MapHealthChecks("/health").AllowAnonymous();
            var starting = app.StartAsync(timeout.Token);
            await called.Task.WaitAsync(timeout.Token);
            starting.IsCompleted.ShouldBeFalse();
            app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single().ShouldBe("http://127.0.0.1:0");
            app.Services.GetRequiredService<LiteReadinessState>().IsReady.ShouldBeFalse();
            app.Services.GetRequiredService<DownloadFlowStartupState>().IsReady.ShouldBeTrue();
            using (var scope = app.Services.CreateScope())
                scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>().FrostStreamUsers.Single().DisplayName.ShouldBe("Admin");
            ready.TrySetResult(new BinaryDownloadResult());
            await starting.WaitAsync(timeout.Token);
            app.Services.GetRequiredService<LiteReadinessState>().IsReady.ShouldBeTrue();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            (await client.GetAsync("/health", timeout.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
            using var me = await client.GetAsync("/api/auth/me", timeout.Token);
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await me.Content.ReadAsStringAsync(timeout.Token)).ShouldContain("Admin");
            var capabilities = await client.GetFromJsonAsync<Shared.Deployment.SystemCapabilities>("/api/system/capabilities", timeout.Token);
            capabilities.ShouldNotBeNull().Backups.Full.ShouldBeTrue();
            capabilities.Backups.Verification.ShouldBeTrue();
            capabilities.Backups.Differential.ShouldBeFalse();
            capabilities.Backups.DeepVerification.ShouldBeFalse();
            capabilities.Backups.PointInTimeRecovery.ShouldBeFalse();
            var repository = await client.GetFromJsonAsync<global::WebAPI.Features.Backups.Models.BackupRepositoryResponse>("/api/global/backups", timeout.Token);
            repository.ShouldNotBeNull().DatabasePath.ShouldBe(Path.Combine(dir, "test.db"));
            repository.BackupDirectory.ShouldBe(Path.Combine(dir, "backups"));
            using var created = await client.PostAsJsonAsync("/api/global/backups", new CreateBackupJobRequest("http-snapshot", "full"), timeout.Token);
            created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var job = (await created.Content.ReadFromJsonAsync<BackupJobDto>(timeout.Token)).ShouldNotBeNull();
            job.Status.ShouldBe("completed");
            using var verified = await client.PostAsJsonAsync("/api/global/backups/verify", new VerifyBackupRequest(job.Label), timeout.Token);
            verified.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await verified.Content.ReadFromJsonAsync<BackupJobDto>(timeout.Token)).ShouldNotBeNull().Kind.ShouldBe("verify-quick");
            using var unsupported = await client.PostAsJsonAsync("/api/global/backups", new CreateBackupJobRequest(null, "diff"), timeout.Token);
            unsupported.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await unsupported.Content.ReadAsStringAsync(timeout.Token)).ShouldContain("full SQLite snapshots only");
            using var deep = await client.PostAsJsonAsync("/api/global/backups/verify", new VerifyBackupRequest(job.Label, true), timeout.Token);
            deep.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await deep.Content.ReadAsStringAsync(timeout.Token)).ShouldContain("deep restore verification is not supported");
            using var unknown = await client.PostAsJsonAsync("/api/global/backups/verify", new VerifyBackupRequest("missing.sqlite"), timeout.Token);
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            var jobs = (await client.GetFromJsonAsync<BackupJobDto[]>("/api/global/backups/jobs", timeout.Token)).ShouldNotBeNull();
            jobs.Length.ShouldBe(2);
            (await client.GetFromJsonAsync<BackupJobDto>($"/api/global/backups/jobs/{job.JobId}", timeout.Token)).ShouldNotBeNull().Label.ShouldBe(job.Label);
            using var unsupportedSchedule = await client.PostAsJsonAsync("/api/global/schedules", new
            {
                key = "unsupported-diff", taskType = "backup-diff", intervalSeconds = 3600, timezone = "UTC", enabled = true, catchupPolicy = "Coalesce"
            }, timeout.Token);
            unsupportedSchedule.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await unsupportedSchedule.Content.ReadAsStringAsync(timeout.Token)).ShouldContain("Differential backup schedules are not supported");
            var hosted = app.Services.GetServices<IHostedService>().ToList();
            hosted.OfType<LiteSchemaInitializationService>().Count().ShouldBe(1);
            hosted.OfType<LiteReadinessService>().Count().ShouldBe(1);
            var scheduler = await app.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(timeout.Token);
            scheduler.IsStarted.ShouldBeTrue();
            (await scheduler.CheckExists(new JobKey("backup-full", "scheduled-tasks"), timeout.Token)).ShouldBeTrue();
            (await scheduler.CheckExists(new JobKey("backup-diff", "scheduled-tasks"), timeout.Token)).ShouldBeFalse();
            await app.Services.GetRequiredService<global::Scheduler.MaintenanceTasks.IBackupScheduler>()
                .QueueBackupAsync(new("backup-full", "backup-full", NodaTime.SystemClock.Instance.GetCurrentInstant(), "test-backup-full", 0, false), timeout.Token);
            var snapshots = (await client.GetFromJsonAsync<global::WebAPI.Features.Backups.Models.BackupRepositoryResponse>("/api/global/backups", timeout.Token)).ShouldNotBeNull();
            snapshots.Backups.Count.ShouldBe(2);
            await app.StopAsync(timeout.Token);
            app.Services.GetRequiredService<LiteReadinessState>().IsReady.ShouldBeFalse();
            scheduler.IsShutdown.ShouldBeTrue();
        }
        finally
        {
            Quartz.Logging.LogContext.SetCurrentLogProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Failed_Required_Initialization_Prevents_Readiness_And_Quartz_Startup()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var builder = Builder(dir);
            ReplaceDownloader(builder, Task.FromException<BinaryDownloadResult>(new InvalidOperationException("binary initialization failed")));
            await using var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            await Should.ThrowAsync<InvalidOperationException>(() => app.StartAsync(timeout.Token));
            app.Services.GetRequiredService<LiteReadinessState>().IsReady.ShouldBeFalse();
            (await app.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(timeout.Token)).IsStarted.ShouldBeFalse();
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            Quartz.Logging.LogContext.SetCurrentLogProvider(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Dedicated_Lite_Host_Defaults_To_SQLite_And_Rejects_Full_Or_Disabled_SQLite()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.ConfigureLiteHost();
        builder.Configuration["Deployment:Mode"].ShouldBe("Lite");
        builder.Configuration["Persistence:Sqlite:Enabled"].ShouldBe("true");
        builder.Configuration["Persistence:Sqlite:Enabled"] = "false";
        Should.Throw<InvalidOperationException>(() => builder.ConfigureLiteHost());
        builder.Configuration["Deployment:Mode"] = "Full";
        Should.Throw<InvalidOperationException>(() => builder.ConfigureLiteHost());
    }
}
