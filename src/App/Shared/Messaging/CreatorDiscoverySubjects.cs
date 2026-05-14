namespace Shared.Messaging;

public static class CreatorDiscoverySubjects
{
    public const string CreateSource = "fs.creator-source.create";
    public const string UpdateSource = "fs.creator-source.update";
    public const string GetSource = "fs.creator-source.get";
    public const string ListSources = "fs.creator-source.list";
    public const string ListEnabledSourcesForScan = "fs.creator-source.list-enabled-for-scan";
    public const string DeleteSource = "fs.creator-source.delete";
    public const string UpsertDiscoveredMediaBatch = "fs.creator-source.discovery.upsert-batch";
}
