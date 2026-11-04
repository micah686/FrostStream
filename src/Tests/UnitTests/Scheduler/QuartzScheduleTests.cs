using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Quartz;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shared.Database;
using Shared.Deployment;
using Shared.Messaging;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Scheduler;

public sealed class QuartzScheduleTests
{
    [Test]
    public void QuartzScheduleFactory_Builds_Interval_And_Cron_Definitions_And_Rejects_Missing_Triggers()
    {
        var intervalTask = Task(TaskTypeRegistry.DatabaseMaintenance, intervalSeconds: 90);
        QuartzScheduleFactory.JobKeyFor("named").Name.ShouldBe("named");
        var job = QuartzScheduleFactory.BuildJob(intervalTask);
        job.Key.ShouldBe(QuartzScheduleFactory.JobKeyFor(intervalTask));
        job.JobType.ShouldBe(typeof(global::Scheduler.Jobs.DatabaseMaintenanceJob));
        job.JobDataMap.GetString(QuartzScheduleFactory.ScheduleKeyData).ShouldBe("interval");
        job.JobDataMap.GetInt(QuartzScheduleFactory.RetentionDaysData).ShouldBe(14);
        job.JobDataMap.GetBoolean(QuartzScheduleFactory.IncludeFailedData).ShouldBeTrue();

        var interval = QuartzScheduleFactory.BuildTrigger(intervalTask).ShouldBeAssignableTo<ISimpleTrigger>()!;
        interval.Key.Name.ShouldBe("interval");
        interval.RepeatInterval.ShouldBe(TimeSpan.FromSeconds(90));
        interval.MisfireInstruction.ShouldBe(SimpleTriggerMisfireInstruction.NextWithRemainingCount);

        var cronTask = Task(TaskTypeRegistry.ChannelScanRefresh, cron: "0 0 0 1 1 ?", timezone: "UTC");
        var cron = QuartzScheduleFactory.BuildTrigger(cronTask).ShouldBeAssignableTo<ICronTrigger>()!;
        cron.CronExpressionString.ShouldBe("0 0 0 1 1 ?");
        cron.TimeZone.Id.ShouldBe("UTC");
        cron.MisfireInstruction.ShouldBe(CronTriggerMisfireInstruction.DoNothing);

        Should.Throw<SchedulerException>(() => QuartzScheduleFactory.BuildTrigger(Task(TaskTypeRegistry.DatabaseMaintenance)));
        Should.Throw<SchedulerException>(() => QuartzScheduleFactory.BuildJob(Task("not-registered")));
    }

    [Test]
    public async Task Registrar_Deletes_Unsupported_And_Unknown_Tasks_And_Replaces_Registered_Jobs()
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.DeleteJob(Arg.Any<JobKey>(), Arg.Any<CancellationToken>()).Returns(true);
        var liteCapabilities = new SystemCapabilities("Lite", new(true, false, false), new(false),
            new("SQLite", true, false, false, true, false));
        var registrar = new QuartzJobRegistrar(NullLogger<QuartzJobRegistrar>.Instance, liteCapabilities);

        await registrar.RegisterAsync(scheduler, Task(TaskTypeRegistry.BackupDiff, intervalSeconds: 60));
        await scheduler.Received(1).DeleteJob(new JobKey("interval", "scheduled-tasks"), Arg.Any<CancellationToken>());
        await scheduler.DidNotReceive().ScheduleJob(Arg.Any<IJobDetail>(), Arg.Any<ITrigger>(), Arg.Any<ScheduleJobOptions>(), Arg.Any<CancellationToken>());

        scheduler.ClearReceivedCalls();
        await registrar.RegisterAsync(scheduler, Task("unknown", key: "unknown", intervalSeconds: 60));
        await scheduler.DidNotReceive().DeleteJob(Arg.Any<JobKey>(), Arg.Any<CancellationToken>());

        scheduler.ClearReceivedCalls();
        await registrar.RegisterAsync(scheduler, Task(TaskTypeRegistry.DatabaseMaintenance, key: "known", intervalSeconds: 60));
        await scheduler.Received(1).DeleteJob(new JobKey("known", "scheduled-tasks"), Arg.Any<CancellationToken>());
        await scheduler.Received(1).ScheduleJob(Arg.Is<IJobDetail>(x => x.Key.Name == "known"),
            Arg.Is<ITrigger>(x => x.Key.Name == "known"), Arg.Any<ScheduleJobOptions>(), Arg.Any<CancellationToken>());

        scheduler.ClearReceivedCalls();
        await registrar.DeleteAsync(scheduler, "removed");
        await scheduler.Received(1).DeleteJob(new JobKey("removed", "scheduled-tasks"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Registrar_Registers_Differential_Backups_When_Capability_Allows_Them()
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.DeleteJob(Arg.Any<JobKey>(), Arg.Any<CancellationToken>()).Returns(false);
        var fullCapabilities = new SystemCapabilities("Full", new(true, false, false), new(true),
            new("PostgreSQL", true, true, true, true, true));
        var registrar = new QuartzJobRegistrar(NullLogger<QuartzJobRegistrar>.Instance, fullCapabilities);

        await registrar.RegisterAsync(scheduler, Task(TaskTypeRegistry.BackupDiff, key: "diff", cron: "0 0 0 * * ?"));

        await scheduler.Received(1).ScheduleJob(Arg.Is<IJobDetail>(x => x.JobType == typeof(global::Scheduler.Jobs.BackupJob)),
            Arg.Is<ITrigger>(x => x is ICronTrigger), Arg.Any<ScheduleJobOptions>(), Arg.Any<CancellationToken>());
    }

    private static ScheduledTaskDto Task(string taskType, string key = "interval", int? intervalSeconds = null,
        string? cron = null, string timezone = "UTC") => new()
    {
        Id = 1,
        Key = key,
        TaskType = taskType,
        Cron = cron,
        IntervalSeconds = intervalSeconds,
        Timezone = timezone,
        Enabled = true,
        CatchupPolicy = ScheduleCatchupPolicy.Coalesce,
        RetentionDays = 14,
        IncludeFailed = true,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
    };
}
