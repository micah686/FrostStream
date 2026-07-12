namespace Shared.Imports;

/// <summary>
/// Conventions for the static "incoming" folder that local media import scans read from.
///
/// The folder location is operator-configured (not chosen per-import by the admin UI). A
/// worker creates it at startup; admins drop media files (with optional info.json/NFO/
/// thumbnail/caption sidecars) into it and start a scan from the import wizard.
/// </summary>
public static class LocalImportIncoming
{
    /// <summary>Default folder name for the incoming import root.</summary>
    public const string FolderName = "incoming";

    /// <summary>
    /// Marker stored in the session/item <c>SourceRoot</c> column. The real absolute path is
    /// worker-local (the configured incoming root), so persistence records the logical name.
    /// </summary>
    public const string SourceRootMarker = "incoming";

    /// <summary>Creates the incoming folder if missing.</summary>
    public static void EnsureScaffold(string incomingRoot)
    {
        if (string.IsNullOrWhiteSpace(incomingRoot))
            throw new ArgumentException("Incoming root is required.", nameof(incomingRoot));

        Directory.CreateDirectory(incomingRoot);
    }
}
