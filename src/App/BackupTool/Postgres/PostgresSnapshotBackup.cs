namespace BackupTool;

/// <summary>
/// Quick logical snapshot: a per-database <c>pg_dump</c> in custom format. This reproduces the
/// original BackupTool behavior and is the default mode.
/// </summary>
internal sealed class PostgresSnapshotBackup(PostgresOptions options, PostgresToolRunner runner) : IPostgresBackupStrategy
{
    public PostgresBackupMode Mode => PostgresBackupMode.Snapshot;

    public async Task<BackupComponent> BackupAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var postgresDir = Path.Combine(backupDirectory, "postgres");
        Directory.CreateDirectory(postgresDir);

        foreach (var database in options.Databases)
        {
            await runner.RunAsync(
                "pg_dump",
                [
                    .. options.ConnectionArgs(),
                    "-F", "c",
                    "-d", database,
                    "-f", Path.Combine(postgresDir, $"{database}.dump")
                ],
                cancellationToken: cancellationToken);
        }

        return new BackupComponent("postgres", "logical-pg-dump", options.Databases);
    }
}
