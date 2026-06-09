using NodaTime;

namespace Shared.Messaging;

public static class BackgroundJobRequestFactory
{
    public const string ManualScheduleKey = "manual";
    public const string ManualSearchReindexTaskType = "manual_search_reindex";

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

    public static string BuildIdempotencyKey(string taskType, string scheduleKey, Instant dueWindowUtc)
        => $"{taskType}:{scheduleKey}:{dueWindowUtc:uuuu-MM-ddTHH:mm:ss'Z'}";
}
