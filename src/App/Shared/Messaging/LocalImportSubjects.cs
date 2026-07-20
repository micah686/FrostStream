namespace Shared.Messaging;

public static class LocalImportSubjects
{
    public const string PrepareLocalImportFileCommand = "import.cmd.prepare-local-file";
    public const string LocalImportFilePrepared = "import.evt.local-file-prepared";
    public const string LocalImportFilePrepareFailed = "import.evt.local-file-prepare-failed";

    public const string ScanLocalImportSourceCommand = "import.cmd.scan-source";
    public const string ProbeImportSessionItemsCommand = "import.cmd.probe-items";
    public const string ImportSessionItemsProbed = "import.evt.items-probed";
    public const string ImportSessionItemsProbeFailed = "import.evt.items-probe-failed";

    public const string DeleteLocalImportSourceCommand = "import.cmd.delete-source";
    public const string EnrichImportSessionItemCommand = "import.cmd.enrich-item";
    public const string ImportSessionItemEnriched = "import.evt.item-enriched";
    public const string ImportSessionItemEnrichFailed = "import.evt.item-enrich-failed";
    public const string BrowseIncomingRequest = "import.req.browse-incoming";
    public const string RefreshMetadataRequest = "import.req.refresh-metadata";

    public static string Tagged(string baseSubject, string tag) => $"{baseSubject}.{tag}";

    public static string PrepareLocalImportFileCommandForTag(string tag) => Tagged(PrepareLocalImportFileCommand, tag);

    public static string DeleteLocalImportSourceCommandForTag(string tag) => Tagged(DeleteLocalImportSourceCommand, tag);

    public static string ScanLocalImportSourceCommandForTag(string tag) => Tagged(ScanLocalImportSourceCommand, tag);

    public static string ProbeImportSessionItemsCommandForTag(string tag) => Tagged(ProbeImportSessionItemsCommand, tag);

    public static string EnrichImportSessionItemCommandForTag(string tag) => Tagged(EnrichImportSessionItemCommand, tag);

    public static string BrowseIncomingRequestForTag(string tag) => Tagged(BrowseIncomingRequest, tag);

    public static string RefreshMetadataRequestForTag(string tag) => Tagged(RefreshMetadataRequest, tag);
}
