namespace Shared.Messaging;

public static class LocalImportSubjects
{
    public const string LocalMediaImportRequested = "import.local-media.requested";

    public const string ReadLocalImportManifestCommand = "import.cmd.read-manifest";
    public const string LocalImportManifestRead = "import.evt.manifest-read";
    public const string LocalImportManifestReadFailed = "import.evt.manifest-read-failed";

    public const string PrepareLocalImportFileCommand = "import.cmd.prepare-local-file";
    public const string LocalImportFilePrepared = "import.evt.local-file-prepared";
    public const string LocalImportFilePrepareFailed = "import.evt.local-file-prepare-failed";

    public static string Tagged(string baseSubject, string tag) => $"{baseSubject}.{tag}";

    public static string ReadLocalImportManifestCommandForTag(string tag) => Tagged(ReadLocalImportManifestCommand, tag);

    public static string PrepareLocalImportFileCommandForTag(string tag) => Tagged(PrepareLocalImportFileCommand, tag);
}
