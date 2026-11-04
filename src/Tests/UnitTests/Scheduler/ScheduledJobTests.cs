using NodaTime;
using NSubstitute;
using Quartz;
using Scheduler.ChannelTasks;
using Scheduler.Jobs;
using Scheduler.MaintenanceTasks;
using Scheduler.Scheduling;
using Scheduler.Triggers;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Scheduler;

public sealed class ScheduledJobTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 3, 2, 0);

    [Test]
    public async Task Jobs_Convert_Quartz_Data_And_Queue_The_Matching_Task()
    {
        var cancellationToken = new CancellationTokenSource().Token;
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Now);

        var asset = Substitute.For<IChannelAssetRefresher>();
        asset.QueueAssetRefreshAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new ChannelAssetRefreshJob(asset, clock).Execute(CreateExecutionContext(TaskTypeRegistry.ChannelAssetRefresh), cancellationToken);
        await asset.Received(1).QueueAssetRefreshAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.ChannelAssetRefresh)), cancellationToken);

        var scanFull = Substitute.For<IChannelScanFullScheduler>();
        scanFull.QueueScanFullAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new ChannelScanFullJob(scanFull, clock).Execute(CreateExecutionContext(TaskTypeRegistry.ChannelScanFull), cancellationToken);
        await scanFull.Received(1).QueueScanFullAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.ChannelScanFull)), cancellationToken);

        var scanRefresh = Substitute.For<IChannelScanRefresher>();
        scanRefresh.QueueScanRefreshAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new ChannelScanRefreshJob(scanRefresh, clock).Execute(CreateExecutionContext(TaskTypeRegistry.ChannelScanRefresh), cancellationToken);
        await scanRefresh.Received(1).QueueScanRefreshAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.ChannelScanRefresh)), cancellationToken);

        var stale = Substitute.For<IStaleEntryCleanupScheduler>();
        stale.QueueCleanupAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new DatabaseStaleMediaCleanupJob(stale, clock).Execute(CreateExecutionContext(TaskTypeRegistry.DatabaseStaleMediaCleanup), cancellationToken);
        await stale.Received(1).QueueCleanupAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.DatabaseStaleMediaCleanup)), cancellationToken);

        var maintenance = Substitute.For<IDatabaseMaintenanceScheduler>();
        maintenance.QueueMaintenanceAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new DatabaseMaintenanceJob(maintenance, clock).Execute(CreateExecutionContext(TaskTypeRegistry.DatabaseMaintenance), cancellationToken);
        await maintenance.Received(1).QueueMaintenanceAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.DatabaseMaintenance)), cancellationToken);

        var dbReindex = Substitute.For<IDatabaseMaintenanceReindexScheduler>();
        dbReindex.QueueReindexAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new DatabaseMaintenanceReindexJob(dbReindex, clock).Execute(CreateExecutionContext(TaskTypeRegistry.DatabaseMaintenanceReindex), cancellationToken);
        await dbReindex.Received(1).QueueReindexAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.DatabaseMaintenanceReindex)), cancellationToken);

        var searchReindex = Substitute.For<ISearchReindexScheduler>();
        searchReindex.QueueReindexAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new SearchReindexJob(searchReindex, clock).Execute(CreateExecutionContext(TaskTypeRegistry.SearchReindex), cancellationToken);
        await searchReindex.Received(1).QueueReindexAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.SearchReindex)), cancellationToken);

        var history = Substitute.For<IDownloadHistoryCleanupScheduler>();
        history.QueueCleanupAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new DownloadHistoryCleanupJob(history, clock).Execute(CreateExecutionContext(TaskTypeRegistry.DownloadHistoryCleanup), cancellationToken);
        await history.Received(1).QueueCleanupAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.DownloadHistoryCleanup)), cancellationToken);

        var imports = Substitute.For<IImportSessionCleanupScheduler>();
        imports.QueueCleanupAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        await new ImportSessionCleanupJob(imports, clock).Execute(CreateExecutionContext(TaskTypeRegistry.ImportSessionCleanup), cancellationToken);
        await imports.Received(1).QueueCleanupAsync(Arg.Is<ScheduledJobContext>(x => Matches(x, TaskTypeRegistry.ImportSessionCleanup)), cancellationToken);

        var backup = Substitute.For<IBackupScheduler>();
        backup.QueueBackupAsync(Arg.Any<ScheduledJobContext>(), cancellationToken).Returns(Task.CompletedTask);
        var backupContext = CreateExecutionContext(TaskTypeRegistry.BackupFull, scheduledFireTime: DateTimeOffset.UtcNow);
        await new BackupJob(backup, clock).Execute(backupContext, cancellationToken);
        await backup.Received(1).QueueBackupAsync(Arg.Is<ScheduledJobContext>(x => x.TaskType == TaskTypeRegistry.BackupFull && x.DueWindowUtc != Now), cancellationToken);
    }

    [Test]
    public async Task Job_Rejects_Quartz_Data_Without_Required_Keys()
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(new JobDataMap());
        var scheduler = Substitute.For<IDatabaseMaintenanceScheduler>();
        var job = new DatabaseMaintenanceJob(scheduler, Substitute.For<IClock>());

        await Should.ThrowAsync<JobExecutionException>(async () => await job.Execute(context, CancellationToken.None));
        await scheduler.DidNotReceiveWithAnyArgs().QueueMaintenanceAsync(default!, default);
    }

    private static IJobExecutionContext CreateExecutionContext(string? taskType = null, DateTimeOffset? scheduledFireTime = null)
    {
        taskType ??= TaskTypeRegistry.ChannelAssetRefresh;
        var map = new JobDataMap
        {
            [QuartzScheduleFactory.ScheduleKeyData] = "scheduled-key",
            [QuartzScheduleFactory.TaskTypeData] = taskType,
            [QuartzScheduleFactory.RetentionDaysData] = 42,
            [QuartzScheduleFactory.IncludeFailedData] = true
        };
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(map);
        context.ScheduledFireTimeUtc.Returns(scheduledFireTime);
        return context;
    }

    private static bool Matches(ScheduledJobContext context, string taskType)
        => context.ScheduleKey == "scheduled-key"
            && context.TaskType == taskType
            && context.DueWindowUtc == Now
            && context.IdempotencyKey == $"{taskType}:scheduled-key:2026-08-03T02:00:00Z"
            && context.RetentionDays == 42
            && context.IncludeFailed;
}
