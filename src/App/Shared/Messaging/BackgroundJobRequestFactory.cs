using NodaTime;

namespace Shared.Messaging;

public static class BackgroundJobRequestFactory
{
    public const string ManualScheduleKey = "manual";
    public const string ManualSearchReindexTaskType = "manual_search_reindex";
    public const string ManualDatabaseMaintenanceReindexTaskType = "manual_database_maintenance_reindex";
    public const string ManualLiveChatBackfillTaskType = "manual_live_chat_backfill";

    /// <summary>
    /// True for the sentinel keys API endpoints stamp on a request they raise by hand (<c>manual</c>,
    /// <c>manual-channel-download</c>, …). Persisted schedule keys are constrained to
    /// <c>^[a-z0-9-]{2,100}$</c> and none of the seeded ones start with "manual", so the prefix is a
    /// safe discriminator between "a schedule fired this" and "a person asked for this".
    /// </summary>
    public static bool IsManualScheduleKey(string? scheduleKey)
        => scheduleKey is not null
           && scheduleKey.StartsWith(ManualScheduleKey, StringComparison.OrdinalIgnoreCase);

    public static SearchReindexRequested CreateSearchReindex(
        string scheduleKey,
        string taskType,
        Instant dueWindowUtc,
        Instant occurredAt)
    {
        var idempotencyKey = BuildIdempotencyKey(taskType, scheduleKey, dueWindowUtc);
        return new SearchReindexRequested
        {
            ScheduleKey = scheduleKey,
            TaskType = taskType,
            DueWindowUtc = dueWindowUtc,
            IdempotencyKey = idempotencyKey,
            OccurredAt = occurredAt
        };
    }

    public static DatabaseMaintenanceReindexRequested CreateDatabaseMaintenanceReindex(
        string scheduleKey,
        string taskType,
        Instant dueWindowUtc,
        Instant occurredAt)
    {
        var idempotencyKey = BuildIdempotencyKey(taskType, scheduleKey, dueWindowUtc);
        return new DatabaseMaintenanceReindexRequested
        {
            ScheduleKey = scheduleKey,
            TaskType = taskType,
            DueWindowUtc = dueWindowUtc,
            IdempotencyKey = idempotencyKey,
            OccurredAt = occurredAt
        };
    }

    public static LiveChatBackfillRequested CreateLiveChatBackfill(
        string scheduleKey,
        string taskType,
        Instant dueWindowUtc,
        Instant occurredAt,
        Guid? targetMediaGuid = null,
        bool force = false)
    {
        // Targeted runs carry the media GUID in the key so re-running one video is not swallowed
        // as a duplicate of the library-wide sweep in the same window.
        var idempotencyKey = targetMediaGuid is { } guid
            ? $"{BuildIdempotencyKey(taskType, scheduleKey, dueWindowUtc)}:{guid:N}"
            : BuildIdempotencyKey(taskType, scheduleKey, dueWindowUtc);
        return new LiveChatBackfillRequested
        {
            ScheduleKey = scheduleKey,
            TaskType = taskType,
            DueWindowUtc = dueWindowUtc,
            IdempotencyKey = idempotencyKey,
            OccurredAt = occurredAt,
            TargetMediaGuid = targetMediaGuid,
            Force = force
        };
    }

    public static string BuildIdempotencyKey(string taskType, string scheduleKey, Instant dueWindowUtc)
        => $"{taskType}:{scheduleKey}:{dueWindowUtc:uuuu-MM-ddTHH:mm:ss'Z'}";
}
