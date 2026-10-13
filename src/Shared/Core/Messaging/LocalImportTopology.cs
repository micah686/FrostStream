
namespace Shared.Messaging;

public static class LocalImportTopology
{
    public const string StreamNameValue = "FROSTSTREAM_IMPORT";
    public static readonly string[] SubjectFilters = ["import.cmd.>", "import.evt.>"];
    public const string ManifestObjectStoreBucket = "local-media-import-manifests";

    public const string DataBridgeQueueGroup = "databridge-imports";
    public const string WorkerQueueGroup = "workers-imports";

    public const string LocalImportFilePreparedConsumer = "databridge-local-import-file-prepared";
    public const string LocalImportFilePrepareFailedConsumer = "databridge-local-import-file-prepare-failed";
    public const string WorkerPrepareLocalImportFileConsumer = "worker-prepare-local-import-file";
    public const string WorkerDeleteLocalImportSourceConsumer = "worker-delete-local-import-source";
    public const string WorkerScanLocalImportSourceConsumer = "worker-scan-local-import-source";
    public const string WorkerProbeImportSessionItemsConsumer = "worker-probe-import-session-items";
    public const string WorkerEnrichImportSessionItemConsumer = "worker-enrich-import-session-item";
    public const string ImportSessionItemsProbedConsumer = "databridge-import-session-items-probed";
    public const string ImportSessionItemEnrichedConsumer = "databridge-import-session-item-enriched";
    public const string ImportSessionItemEnrichFailedConsumer = "databridge-import-session-item-enrich-failed";

}
