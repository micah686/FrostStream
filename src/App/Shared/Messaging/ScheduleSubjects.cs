namespace Shared.Messaging;

/// <summary>
/// NATS subjects used by the BgProcessor / Scheduler design.
///
/// CRUD subjects use request/reply (queue group <c>databridge-schedules</c>); the
/// <c>changed</c> broadcast is fan-out so every Scheduler replica updates its in-memory
/// Quartz state. Trigger commands (<c>fs.cleanup.*</c> etc.) live on the durable
/// <see cref="BackgroundJobsTopology"/> stream — Scheduler publishes, DataBridge / Worker
/// / MediaProcessor consume.
/// </summary>
public static class ScheduleSubjects
{
    // Schedule CRUD (request/reply).
    public const string CreateSchedule = "fs.schedules.create";
    public const string UpdateSchedule = "fs.schedules.update";
    public const string GetSchedule    = "fs.schedules.get";
    public const string ListSchedules  = "fs.schedules.list";
    public const string DeleteSchedule = "fs.schedules.delete";

    // Scheduler -> DataBridge (request/reply).
    public const string ListActive  = "fs.schedules.activelist";
    public const string ListOverdue = "fs.schedules.overdue";

    // Result-reporting (publish).
    public const string MarkAttempt = "fs.schedules.markattempt";
    public const string MarkSuccess = "fs.schedules.marksuccess";

    // Broadcast: schedule definition was created/updated/deleted.
    public const string ScheduleChanged = "fs.schedules.changed";

    // Trigger commands (JetStream durable).
    public const string OrphanMetadataCleanupRequest = "fs.cleanup.metadata.orphans.request";
}
