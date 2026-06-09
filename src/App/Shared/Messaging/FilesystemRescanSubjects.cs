namespace Shared.Messaging;

public static class FilesystemRescanSubjects
{
    /// <summary>Request/reply: Worker asks DataBridge which storage keys have database content.</summary>
    public const string StorageKeys = "fs.media.filesystem.storage-keys";

    /// <summary>Request/reply: Worker asks DataBridge to reconcile one uploaded storage listing.</summary>
    public const string Reconcile = "fs.media.filesystem.reconcile";
}
