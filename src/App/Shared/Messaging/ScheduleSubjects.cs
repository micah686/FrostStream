namespace Shared.Messaging;

public static class ScheduleSubjects
{
    public const string Create = "fs.schedules.create";
    public const string Update = "fs.schedules.update";
    public const string Get = "fs.schedules.get";
    public const string List = "fs.schedules.list";
    public const string Delete = "fs.schedules.delete";
    public const string ActiveList = "fs.schedules.activelist";
    public const string Overdue = "fs.schedules.overdue";
    public const string MarkAttempt = "fs.schedules.markattempt";
    public const string MarkSuccess = "fs.schedules.marksuccess";
    public const string Changed = "fs.schedules.changed";

    public const string OrphanMetadataCleanupRequest = "fs.cleanup.metadata.orphans.request";
}
