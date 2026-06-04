using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shouldly;
using TUnit.Core;

namespace UnitTests.DataBridge;

public sealed class ScheduledTasksRepositoryTests
{
    [Test]
    public async Task Create_Computes_Next_Due_For_Interval_Tasks()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var repo = new ScheduledTasksRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var created = await repo.CreateAsync(new ScheduledTaskEntity
        {
            Key = "asset-refresh",
            TaskType = "channel_asset_refresh",
            IntervalSeconds = 300,
            Timezone = "UTC",
            Enabled = true
        });

        created.NextDueAt.ShouldBe(DataBridgeTestHelpers.Now.Plus(Duration.FromMinutes(5)));
    }

    [Test]
    public async Task ListActive_And_ListOverdue_Filter_Enabled_Coalescing_Tasks()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var now = DataBridgeTestHelpers.Now;
        db.ScheduledTasks.AddRange(
            Task("overdue", enabled: true, ScheduleCatchupPolicy.Coalesce, now.Minus(Duration.FromMinutes(1))),
            Task("future", enabled: true, ScheduleCatchupPolicy.Coalesce, now.Plus(Duration.FromMinutes(1))),
            Task("disabled", enabled: false, ScheduleCatchupPolicy.Coalesce, now.Minus(Duration.FromMinutes(1))),
            Task("skip", enabled: true, ScheduleCatchupPolicy.Skip, now.Minus(Duration.FromMinutes(1))));
        await db.SaveChangesAsync();
        var repo = new ScheduledTasksRepository(db, new FixedClock(now));

        var active = await repo.ListActiveAsync();
        var overdue = await repo.ListOverdueAsync();

        active.Select(x => x.Key).ShouldBe(["future", "overdue", "skip"]);
        overdue.Select(x => x.Key).ShouldBe(["overdue"]);
    }

    [Test]
    public async Task MarkAttempt_Success_And_Failure_Update_Run_Status_And_Next_Due()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        var now = DataBridgeTestHelpers.Now;
        db.ScheduledTasks.Add(new ScheduledTaskEntity
        {
            Key = "cleanup",
            TaskType = "processed_message_cleanup",
            IntervalSeconds = 60,
            Timezone = "UTC",
            Enabled = true,
            CatchupPolicy = ScheduleCatchupPolicy.Coalesce
        });
        await db.SaveChangesAsync();
        var repo = new ScheduledTasksRepository(db, new FixedClock(now));

        await repo.MarkAttemptAsync("cleanup", now);
        var afterAttempt = await db.ScheduledTasks.SingleAsync(x => x.Key == "cleanup");
        afterAttempt.LastAttemptAt.ShouldBe(now);
        afterAttempt.LastRunStatus.ShouldBe(ScheduleRunStatus.InProgress);

        var successAt = now.Plus(Duration.FromMinutes(2));
        var afterSuccess = await repo.MarkSuccessAsync("cleanup", successAt);
        afterSuccess.ShouldNotBeNull();
        afterSuccess.LastSuccessAt.ShouldBe(successAt);
        afterSuccess.LastRunStatus.ShouldBe(ScheduleRunStatus.Completed);
        afterSuccess.NextDueAt.ShouldBe(successAt.Plus(Duration.FromMinutes(1)));

        var failureAt = now.Plus(Duration.FromMinutes(5));
        var afterFailure = await repo.MarkFailureAsync("cleanup", failureAt);
        afterFailure.ShouldNotBeNull();
        afterFailure.LastRunStatus.ShouldBe(ScheduleRunStatus.Failed);
        afterFailure.NextDueAt.ShouldBe(failureAt.Plus(Duration.FromMinutes(1)));
    }

    [Test]
    public async Task Update_Disabled_Task_Clears_Next_Due()
    {
        await using var db = DataBridgeTestHelpers.CreateDb();
        db.ScheduledTasks.Add(new ScheduledTaskEntity
        {
            Key = "cleanup",
            TaskType = "processed_message_cleanup",
            IntervalSeconds = 60,
            Timezone = "UTC",
            Enabled = true,
            NextDueAt = DataBridgeTestHelpers.Now
        });
        await db.SaveChangesAsync();
        var repo = new ScheduledTasksRepository(db, new FixedClock(DataBridgeTestHelpers.Now));

        var updated = await repo.UpdateAsync(new ScheduledTaskEntity
        {
            Key = "cleanup",
            TaskType = "processed_message_cleanup",
            IntervalSeconds = 60,
            Timezone = "UTC",
            Enabled = false
        });

        updated.ShouldNotBeNull();
        updated.Enabled.ShouldBeFalse();
        updated.NextDueAt.ShouldBeNull();
        updated.LastUpdated.ShouldBe(DataBridgeTestHelpers.Now);
    }

    private static ScheduledTaskEntity Task(
        string key,
        bool enabled,
        ScheduleCatchupPolicy catchupPolicy,
        Instant? nextDueAt)
        => new()
        {
            Key = key,
            TaskType = "task",
            IntervalSeconds = 60,
            Timezone = "UTC",
            Enabled = enabled,
            CatchupPolicy = catchupPolicy,
            NextDueAt = nextDueAt
        };
}
