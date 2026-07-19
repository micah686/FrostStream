using System.Globalization;

namespace BackupService;

/// <summary>
/// Full physical cluster backup via <c>pg_basebackup</c> (tar + gzip, streamed WAL, SHA256
/// backup manifest). This is the base image for point-in-time recovery when paired with an
/// ongoing WAL archive. Requires a replication-capable role, <c>wal_level &gt;= replica</c> and
/// <c>max_wal_senders &gt;= 1</c> on the server (see <c>wal-archive setup</c>).
/// </summary>
internal sealed class PostgresFullBackup(PostgresOptions options, PostgresToolRunner runner) : IPostgresBackupStrategy
{
    public const string BaseBackupDirName = "basebackup";

    public PostgresBackupMode Mode => PostgresBackupMode.Full;

    public async Task<BackupComponent> BackupAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var baseDir = Path.Combine(backupDirectory, "postgres", BaseBackupDirName);
        Directory.CreateDirectory(baseDir);

        await runner.RunAsync(
            "pg_basebackup",
            [
                "-h", options.Host,
                "-p", options.Port.ToString(CultureInfo.InvariantCulture),
                "-U", options.ReplicationUser,
                "-D", baseDir,
                "-F", "t",
                "-z",
                "-X", "stream",
                "-P",
                "--manifest-checksums=SHA256"
            ],
            cancellationToken: cancellationToken);

        return new BackupComponent("postgres", "physical-basebackup", [BaseBackupDirName]);
    }
}
