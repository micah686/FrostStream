namespace AppHost;

/// <summary>
/// Shared filesystem locations for backups, so the Postgres container's pgBackRest repository
/// mount and the BackupService env always agree.
/// </summary>
internal static class BackupPaths
{
    public static string BackupRoot(string sharedStorageRoot)
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT"))
            ? Path.Combine(sharedStorageRoot, "core-backups")
            : Environment.GetEnvironmentVariable("FROSTSTREAM_BACKUP_ROOT")!;

    /// <summary>pgBackRest repository (repo1-path maps here inside the containers).</summary>
    public static string PgBackRestRepoDirectory(string sharedStorageRoot)
        => Path.Combine(BackupRoot(sharedStorageRoot), "pgbackrest");

    /// <summary>Per-backup OpenBao KV exports, keyed by pgBackRest backup label.</summary>
    public static string OpenBaoExportDirectory(string sharedStorageRoot)
        => Path.Combine(BackupRoot(sharedStorageRoot), "openbao");
}
