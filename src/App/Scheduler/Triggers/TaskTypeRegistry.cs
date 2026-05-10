using Quartz;

namespace Scheduler.Triggers;

/// <summary>
/// Maps the persisted <c>task_type</c> string on a schedule row to the Quartz
/// <see cref="IJob"/> that handles it. Adding a new scheduled-job kind is one
/// entry here plus a DI registration of the IJob class.
/// </summary>
public static class TaskTypeRegistry
{
    public const string OrphanMetadataCleanup = "orphan_metadata_cleanup";

    private static readonly Dictionary<string, Type> Map = new(StringComparer.Ordinal)
    {
        [OrphanMetadataCleanup] = typeof(OrphanMetadataCleanupTriggerJob)
    };

    public static bool TryGetJobType(string taskType, out Type jobType)
        => Map.TryGetValue(taskType, out jobType!);

    public static IEnumerable<string> KnownTaskTypes => Map.Keys;
}
