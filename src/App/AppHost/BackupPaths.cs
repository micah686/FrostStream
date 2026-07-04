namespace AppHost;

/// <summary>
/// Shared filesystem locations for backups, so the Postgres container's WAL archive mount and the
/// BackupTool env handed to WebAPI/DataBridge always agree.
/// </summary>
internal static class BackupPaths
{
    public static string BackupRoot(string sharedStorageRoot)
        => Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT")
           ?? Path.Combine(sharedStorageRoot, "core-backups");

    public static string WalArchiveDirectory(string sharedStorageRoot)
        => Path.Combine(sharedStorageRoot, "wal-archive");
}
