namespace BackupTool;

/// <summary>
/// A PostgreSQL backup strategy. Each mode (snapshot, full, wal-archive) writes its output under
/// the backup directory (or an external archive store) and returns the manifest component describing it.
/// </summary>
internal interface IPostgresBackupStrategy
{
    PostgresBackupMode Mode { get; }

    Task<BackupComponent> BackupAsync(string backupDirectory, CancellationToken cancellationToken);
}
