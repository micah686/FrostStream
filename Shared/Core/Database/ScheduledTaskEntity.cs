namespace Shared.Database;
public enum ScheduleCatchupPolicy
{
    Coalesce = 0,
    Skip = 1
}

public enum ScheduleRunStatus
{
    InProgress = 0,
    Completed = 1,
    Failed = 2
}
