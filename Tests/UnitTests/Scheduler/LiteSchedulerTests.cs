using DataBridge;
using DataBridge.Data;
using DataBridge.Messaging;
using DataBridge.Persistence;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Scheduler;
using Scheduler.Databridge;
using Scheduler.Services;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Database;
using Shared.Messaging;
using Shouldly;

namespace UnitTests.Scheduler;

public sealed class LiteSchedulerTests
{
    private static IHost BuildHost(string path)
    {
        // Quartz keeps its logging provider globally; simulated restarts must detach the disposed host.
        Quartz.Logging.LogContext.SetCurrentLogProvider(NullLoggerFactory.Instance);
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Deployment:Mode"] = "Lite";
        builder.Configuration["Persistence:Sqlite:Enabled"] = "true";
        builder.Configuration["Persistence:Sqlite:Path"] = path;
        builder.AddDataBridgePersistence();
        builder.Services.AddScoped<IScheduledTasksRepository, ScheduledTasksRepository>();
        builder.Services.AddHostedService<ScheduleCrudConsumerService>();
        builder.AddSchedulerModule();
        var host = builder.Build();
        host.InitializeDataBridge();
        return host;
    }

    private static async Task Eventually(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition()) await Task.Delay(20, token);
    }

    [Test]
    public async Task Sqlite_Definitions_Fire_Shared_Jobs_And_Reload_After_Restart_With_Unchanged_Triggers()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using (var host = BuildHost(Path.Combine(dir, "test.db")))
            {
                using (var scope = host.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
                    foreach (var seed in db.ScheduledTasks) seed.Enabled = false;
                    await db.SaveChangesAsync(timeout.Token);
                    var repo = scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>();
                    await repo.CreateAsync(new ScheduledTaskEntity { Key = "test-interval", TaskType = TaskTypeRegistry.DatabaseMaintenance, IntervalSeconds = 3600, Enabled = true }, timeout.Token);
                    await repo.CreateAsync(new ScheduledTaskEntity { Key = "test-cron", TaskType = TaskTypeRegistry.ChannelScanRefresh, Cron = "0 0 0 1 1 ?", Timezone = "America/Los_Angeles", Enabled = true }, timeout.Token);
                    await repo.CreateAsync(new ScheduledTaskEntity { Key = "test-disabled", TaskType = TaskTypeRegistry.DatabaseMaintenance, IntervalSeconds = 60, Enabled = false }, timeout.Token);
                }
                var dispatched = new TaskCompletionSource<DatabaseMaintenanceRequested>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var consumerStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var bus = host.Services.GetRequiredService<IMessageBus>();
                var consumer = host.Services.GetRequiredService<IDurableJobConsumer>().ConsumeAsync<DatabaseMaintenanceRequested>(
                    StreamName.From(BackgroundJobsTopology.StreamNameValue), SubjectName.From(BackgroundJobSubjects.DatabaseMaintenanceRequest), async context =>
                    {
                        await bus.PublishAsync(ScheduleSubjects.MarkAttempt,
                            new ScheduleMarkAttemptRequestMessage { Key = context.Message.ScheduleKey, AttemptedAt = context.Message.OccurredAt }, timeout.Token);
                        await context.AckAsync(timeout.Token);
                        dispatched.TrySetResult(context.Message);
                    }, cancellationToken: consumerStop.Token);
                await host.StartAsync(timeout.Token);
                var message = await dispatched.Task.WaitAsync(timeout.Token);
                message.ScheduleKey.ShouldBe("test-interval");
                message.TaskType.ShouldBe(TaskTypeRegistry.DatabaseMaintenance);
                message.IdempotencyKey.ShouldStartWith("db-maintenance:test-interval:");
                var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(timeout.Token);
                scheduler.IsStarted.ShouldBeTrue();
                (await scheduler.CheckExists(JobKeys.ForSchedule("test-disabled"), timeout.Token)).ShouldBeFalse();
                await Eventually(async () =>
                {
                    using var scope = host.Services.CreateScope();
                    return (await scope.ServiceProvider.GetRequiredService<IScheduledTasksRepository>().GetByKeyAsync("test-interval", timeout.Token))!.LastRunStatus == ScheduleRunStatus.InProgress;
                }, timeout.Token);
                await host.StopAsync(timeout.Token);
                scheduler.IsShutdown.ShouldBeTrue();
                consumerStop.Cancel();
                try { await consumer; } catch (OperationCanceledException) { }
            }
            using (var restarted = BuildHost(Path.Combine(dir, "test.db")))
            {
                await restarted.StartAsync(timeout.Token);
                var scheduler = await restarted.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(timeout.Token);
                var cron = (await scheduler.GetTrigger(TriggerKeys.ForSchedule("test-cron"), timeout.Token)).ShouldBeAssignableTo<Quartz.ICronTrigger>()!;
                cron.TimeZone.Id.ShouldBe("America/Los_Angeles");
                cron.MisfireInstruction.ShouldBe(MisfireInstruction.CronTrigger.DoNothing);
                var interval = (await scheduler.GetTrigger(TriggerKeys.ForSchedule("test-interval"), timeout.Token)).ShouldBeAssignableTo<Quartz.ISimpleTrigger>()!;
                interval.RepeatInterval.ShouldBe(TimeSpan.FromHours(1));
                interval.MisfireInstruction.ShouldBe(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount);
                var bus = restarted.Services.GetRequiredService<IMessageBus>();
                var disabled = await bus.RequestAsync<ScheduleUpdateRequestMessage, ScheduleOperationResponseMessage>(ScheduleSubjects.Update,
                    new() { Key = "test-interval", TaskType = TaskTypeRegistry.DatabaseMaintenance, IntervalSeconds = 3600, Enabled = false }, TimeSpan.FromSeconds(5), timeout.Token);
                disabled.ShouldNotBeNull().Success.ShouldBeTrue();
                await Eventually(async () => !await scheduler.CheckExists(JobKeys.ForSchedule("test-interval"), timeout.Token), timeout.Token);
                var created = await bus.RequestAsync<ScheduleCreateRequestMessage, ScheduleOperationResponseMessage>(ScheduleSubjects.Create,
                    new() { Key = "test-new", TaskType = TaskTypeRegistry.ChannelScanRefresh, Cron = "0 0 0 1 1 ?", Enabled = true }, TimeSpan.FromSeconds(5), timeout.Token);
                created.ShouldNotBeNull().Success.ShouldBeTrue();
                await Eventually(() => scheduler.CheckExists(JobKeys.ForSchedule("test-new"), timeout.Token), timeout.Token);
                var deleted = await bus.RequestAsync<ScheduleDeleteRequestMessage, ScheduleOperationResponseMessage>(ScheduleSubjects.Delete,
                    new() { Key = "test-new" }, TimeSpan.FromSeconds(5), timeout.Token);
                deleted.ShouldNotBeNull().Success.ShouldBeTrue();
                await Eventually(async () => !await scheduler.CheckExists(JobKeys.ForSchedule("test-new"), timeout.Token), timeout.Token);
                await restarted.StopAsync(timeout.Token);
            }
        }
        finally
        {
            Quartz.Logging.LogContext.SetCurrentLogProvider(NullLoggerFactory.Instance);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class DelayedSubscriptions : SubscriptionBackgroundService
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task RegisterSubscriptionsAsync(CancellationToken token) => Release.Task.WaitAsync(token);
    }

    [Test]
    public async Task Lite_Quartz_Waits_For_Registration_And_Hydration_And_Does_Not_Start_On_Failure()
    {
        var delayed = new DelayedSubscriptions();
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService>(delayed);
        using var provider = services.BuildServiceProvider();
        var scheduler = Substitute.For<IScheduler>();
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);
        var client = Substitute.For<IDatabridgeClient>();
        var hydrationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definitions = new TaskCompletionSource<ScheduleOperationResponseMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            hydrationRequested.TrySetResult();
            return definitions.Task;
        });
        var hydration = new ScheduleHydrationService(client, factory, Substitute.For<IQuartzJobRegistrar>(), NullLogger<ScheduleHydrationService>.Instance);
        var startup = new LiteSchedulerStartupService(provider, factory, hydration, NullLogger<LiteSchedulerStartupService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await delayed.StartAsync(timeout.Token);
        var starting = startup.StartAsync(timeout.Token);
        starting.IsCompleted.ShouldBeFalse();
        await scheduler.DidNotReceive().Start(Arg.Any<CancellationToken>());
        delayed.Release.TrySetResult();
        await hydrationRequested.Task.WaitAsync(timeout.Token);
        await scheduler.DidNotReceive().Start(Arg.Any<CancellationToken>());
        definitions.TrySetResult(new ScheduleOperationResponseMessage { Success = true, Items = [] });
        await starting;
        await client.Received(1).ListActiveSchedulesAsync(Arg.Any<CancellationToken>());
        await scheduler.Received(1).Start(Arg.Any<CancellationToken>());
        await startup.StopAsync(timeout.Token);
        await scheduler.Received().Shutdown(true, Arg.Any<CancellationToken>());
        await delayed.StopAsync(timeout.Token);
        delayed.Dispose();
        client.ListActiveSchedulesAsync(Arg.Any<CancellationToken>()).Returns(new ScheduleOperationResponseMessage { Success = false });
        scheduler.ClearReceivedCalls();
        await Should.ThrowAsync<InvalidOperationException>(() => startup.StartAsync(timeout.Token));
        await scheduler.DidNotReceive().Start(Arg.Any<CancellationToken>());
        await scheduler.Received().Shutdown(false, Arg.Any<CancellationToken>());
    }
}
