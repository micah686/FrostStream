namespace Shared.Messaging;

public static class FilesystemRescanSubjects
{
    /// <summary>Request/reply: Worker asks DataBridge for the expected file inventory to reconcile against storage.</summary>
    public const string Inventory = "fs.media.filesystem.inventory";

    /// <summary>Request/reply: Worker reports the reconciliation findings for a single storage key back to DataBridge.</summary>
    public const string Report = "fs.media.filesystem.report";
}
