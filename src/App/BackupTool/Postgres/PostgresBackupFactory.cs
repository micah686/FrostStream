namespace BackupTool;

internal static class PostgresBackupFactory
{
    public static IPostgresBackupStrategy Create(PostgresBackupMode mode, PostgresOptions options, PostgresToolRunner runner)
        => mode switch
        {
            PostgresBackupMode.Snapshot => new PostgresSnapshotBackup(options, runner),
            PostgresBackupMode.Full => new PostgresFullBackup(options, runner),
            PostgresBackupMode.WalArchive => new PostgresWalArchive(options),
            _ => throw new InvalidOperationException($"Unsupported backup mode '{mode}'.")
        };

    public static PostgresBackupMode ParseMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "snapshot" => PostgresBackupMode.Snapshot,
            "full" => PostgresBackupMode.Full,
            "wal-archive" or "wal" => PostgresBackupMode.WalArchive,
            _ => throw new InvalidOperationException($"Unknown backup mode '{value}'. Expected snapshot|full|wal-archive.")
        };
}
