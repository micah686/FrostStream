namespace AppHost;

/// <summary>
/// Shared filesystem locations for backups, so the Postgres container's pgBackRest repository
/// mount and the BackupService env always agree.
/// </summary>
internal static class BackupPaths
{
    public static string BackupRoot(string sharedStorageRoot)
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT"))
            ? Path.Combine(sharedStorageRoot, "backups")
            : Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT")!;
}
