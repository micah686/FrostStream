namespace Shared.Messaging;

public static class LocalImportSubjects
{
    public const string LocalMediaImportRequested = "import.local-media.requested";

    public const string PrepareLocalImportFileCommand = "import.cmd.prepare-local-file";
    public const string LocalImportFilePrepared = "import.evt.local-file-prepared";
    public const string LocalImportFilePrepareFailed = "import.evt.local-file-prepare-failed";

    public static string Tagged(string baseSubject, string tag) => $"{baseSubject}.{tag}";

    public static string PrepareLocalImportFileCommandForTag(string tag) => Tagged(PrepareLocalImportFileCommand, tag);
}
