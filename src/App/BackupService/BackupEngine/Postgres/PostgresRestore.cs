using System.Formats.Tar;
using System.IO.Compression;

namespace BackupService;

/// <summary>
/// Cold/offline restore of a PostgreSQL backup, dispatched by the backup mode recorded in the
/// manifest. Snapshot restores are logical (per-database). Full restores rebuild the cluster data
/// directory and, when a recovery target is supplied, configure point-in-time recovery driven by
/// the WAL archive.
/// </summary>
internal sealed class PostgresRestore(PostgresOptions options, PostgresToolRunner runner)
{
    public Task RestoreAsync(string archive, BackupManifest manifest, RestoreTarget target, CancellationToken cancellationToken)
        => manifest.Mode switch
        {
            PostgresBackupMode.Snapshot => RestoreSnapshotAsync(archive, cancellationToken),
            PostgresBackupMode.Full => RestoreFullAsync(archive, target, cancellationToken),
            PostgresBackupMode.WalArchive => throw new InvalidOperationException(
                "A wal-archive backup is not a standalone restore source. Restore a 'full' backup with a recovery "
                + "target (--target-time/--target-lsn/--target-name or --recover-latest) that reads the WAL archive."),
            _ => throw new InvalidOperationException($"Unsupported backup mode '{manifest.Mode}'.")
        };

    private async Task RestoreSnapshotAsync(string archive, CancellationToken cancellationToken)
    {
        foreach (var database in options.Databases)
        {
            var dump = Path.Combine(archive, "postgres", $"{database}.dump");
            if (!File.Exists(dump))
                throw new FileNotFoundException($"Missing PostgreSQL dump for database '{database}'.", dump);

            await runner.RunAsync(
                "dropdb",
                ["--if-exists", .. options.ConnectionArgs(), database],
                cancellationToken: cancellationToken);
            await runner.RunAsync(
                "createdb",
                [.. options.ConnectionArgs(), database],
                cancellationToken: cancellationToken);
            await runner.RunAsync(
                "pg_restore",
                [.. options.ConnectionArgs(), "-d", database, "--clean", "--if-exists", dump],
                cancellationToken: cancellationToken);
        }
    }

    private async Task RestoreFullAsync(string archive, RestoreTarget target, CancellationToken cancellationToken)
    {
        var baseDir = Path.Combine(archive, "postgres", PostgresFullBackup.BaseBackupDirName);
        var baseTar = Path.Combine(baseDir, "base.tar.gz");
        if (!File.Exists(baseTar))
            throw new FileNotFoundException("Full backup is missing base.tar.gz.", baseTar);

        if (string.IsNullOrWhiteSpace(options.PgData))
        {
            PrintFullRestorePlan(baseDir, target);
            return;
        }

        var pgData = Path.GetFullPath(options.PgData);
        var pgCtl = string.IsNullOrWhiteSpace(options.PgCtl) ? options.ToolPath("pg_ctl") : options.PgCtl;

        // Stop the server if it is running; ignore failure (it may already be stopped).
        await ProcessRunner.RunAsync(pgCtl, ["-D", pgData, "stop", "-m", "fast"], throwOnError: false, cancellationToken: cancellationToken);

        ClearDirectory(pgData);
        await ExtractTarGzAsync(baseTar, pgData, cancellationToken);

        var walTar = Path.Combine(baseDir, "pg_wal.tar.gz");
        if (File.Exists(walTar))
            await ExtractTarGzAsync(walTar, Path.Combine(pgData, "pg_wal"), cancellationToken);

        DeleteIfExists(Path.Combine(pgData, "recovery.signal"));
        DeleteIfExists(Path.Combine(pgData, "standby.signal"));

        if (target.HasTarget)
            await ConfigurePointInTimeRecoveryAsync(pgData, target);

        await ProcessRunner.RunAsync(pgCtl, ["-D", pgData, "start", "-w"], cancellationToken: cancellationToken);
    }

    private async Task ConfigurePointInTimeRecoveryAsync(string pgData, RestoreTarget target)
    {
        var archiveDir = target.ArchiveDir
            ?? throw new InvalidOperationException("--archive-dir is required for point-in-time recovery.");
        var tool = options.ToolCommand ?? PostgresWalArchive.ToolCommandPlaceholder;

        var lines = new List<string>
        {
            $"restore_command = '{tool} wal-archive restore %f %p --archive-dir {archiveDir}'",
            "recovery_target_action = 'promote'"
        };

        if (target.Time is not null)
            lines.Add($"recovery_target_time = '{target.Time}'");
        else if (target.Lsn is not null)
            lines.Add($"recovery_target_lsn = '{target.Lsn}'");
        else if (target.Name is not null)
            lines.Add($"recovery_target_name = '{target.Name}'");
        // Otherwise --recover-latest: replay all available WAL (no recovery_target_* line).

        var autoConf = Path.Combine(pgData, "postgresql.auto.conf");
        await File.AppendAllLinesAsync(autoConf, ["", "# Added by FrostStream BackupService restore", .. lines]);
        await File.WriteAllTextAsync(Path.Combine(pgData, "recovery.signal"), string.Empty);
    }

    private void PrintFullRestorePlan(string baseDir, RestoreTarget target)
    {
        Console.WriteLine("Full restore requires --pgdata (the target data directory). No changes were made.");
        Console.WriteLine("Offline steps to perform against a stopped server:");
        Console.WriteLine($"  1. pg_ctl -D <PGDATA> stop -m fast");
        Console.WriteLine($"  2. Empty <PGDATA>");
        Console.WriteLine($"  3. Extract {Path.Combine(baseDir, "base.tar.gz")} into <PGDATA>");
        Console.WriteLine($"  4. Extract {Path.Combine(baseDir, "pg_wal.tar.gz")} into <PGDATA>/pg_wal");
        if (target.HasTarget)
        {
            var archiveDir = target.ArchiveDir ?? "<archive-dir>";
            var tool = options.ToolCommand ?? PostgresWalArchive.ToolCommandPlaceholder;
            Console.WriteLine("  5. Append to <PGDATA>/postgresql.auto.conf:");
            Console.WriteLine($"       restore_command = '{tool} wal-archive restore %f %p --archive-dir {archiveDir}'");
            Console.WriteLine("       recovery_target_action = 'promote'");
            if (target.Time is not null) Console.WriteLine($"       recovery_target_time = '{target.Time}'");
            else if (target.Lsn is not null) Console.WriteLine($"       recovery_target_lsn = '{target.Lsn}'");
            else if (target.Name is not null) Console.WriteLine($"       recovery_target_name = '{target.Name}'");
            Console.WriteLine("  6. touch <PGDATA>/recovery.signal");
            Console.WriteLine("  7. pg_ctl -D <PGDATA> start -w");
        }
        else
        {
            Console.WriteLine("  5. pg_ctl -D <PGDATA> start -w");
        }
    }

    private static async Task ExtractTarGzAsync(string tarGzPath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await using var file = File.OpenRead(tarGzPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancellationToken);
    }

    private static void ClearDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
