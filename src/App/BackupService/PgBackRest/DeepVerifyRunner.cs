namespace BackupService.PgBackRest;

/// <summary>
/// Deep validation: restore a backup into a scratch directory under the backup root, start a
/// throwaway PostgreSQL on it (socket-only, private socket dir, archiving off), and prove the
/// data is really there — the expected databases exist and each contains user tables. The
/// scratch server recovers with --type=immediate (consistency at the end of the base backup,
/// minimal WAL replay) and is torn down and deleted afterwards.
/// </summary>
internal class DeepVerifyRunner(
    BackupServiceOptions options,
    PgBackRestRunner runner,
    ILogger<DeepVerifyRunner> logger)
{
    private string ScratchRoot => Path.Combine(Path.GetFullPath(options.Directory), ".deep-verify");
    private string ScratchPgData => Path.Combine(ScratchRoot, "pgdata");
    private string ScratchSockets => Path.Combine(ScratchRoot, "sock");
    private string ServerLog => Path.Combine(ScratchRoot, "server.log");

    public virtual async Task RunAsync(string? label, IProgress<string> progress, CancellationToken cancellationToken)
    {
        DeleteScratch();
        Directory.CreateDirectory(ScratchPgData);
        Directory.CreateDirectory(ScratchSockets);

        var serverStarted = false;
        try
        {
            progress.Report($"Restoring {(label is null ? "the latest backup" : $"backup {label}")} into the scratch directory…");
            await runner.RestoreAsync(
                label,
                targetTime: null,
                pgDataPath: ScratchPgData,
                immediateTarget: true,
                progress,
                cancellationToken);

            // PostgreSQL refuses to start on a group/world-accessible data directory.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(ScratchPgData, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            progress.Report("Starting a throwaway PostgreSQL on the restored files…");
            await ProcessRunner.RunAsync(
                "pg_ctl",
                [
                    "-D", ScratchPgData,
                    "-l", ServerLog,
                    "-w", "-t", "600",
                    "-o", $"-c listen_addresses='' -c unix_socket_directories='{ScratchSockets}' -c archive_mode=off -c logging_collector=off",
                    "start"
                ],
                cancellationToken: cancellationToken);
            serverStarted = true;

            progress.Report("Checking the restored databases…");
            var databases = (await QueryAsync("postgres", "SELECT datname FROM pg_database WHERE NOT datistemplate", cancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var missing = options.ExpectedDatabases.Except(databases, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Restored cluster is missing expected databases: {string.Join(", ", missing)}.");

            foreach (var database in options.ExpectedDatabases)
            {
                var count = (await QueryAsync(
                    database,
                    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                    + "WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema')",
                    cancellationToken)).Trim();
                if (!long.TryParse(count, out var tables) || tables == 0)
                    throw new InvalidOperationException($"Restored database '{database}' contains no user tables.");
                progress.Report($"Database '{database}': {tables} user tables present.");
            }

            progress.Report("Deep verification passed.");
        }
        catch (Exception) when (LogServerTail())
        {
            throw; // never reached: LogServerTail returns false
        }
        finally
        {
            if (serverStarted)
            {
                var stop = await ProcessRunner.RunAsync(
                    "pg_ctl",
                    ["-D", ScratchPgData, "-m", "fast", "-w", "stop"],
                    throwOnError: false,
                    cancellationToken: CancellationToken.None);
                if (stop.ExitCode != 0)
                    logger.LogWarning("Failed to stop the deep-verify server: {Error}", stop.StandardError);
            }
            DeleteScratch();
        }
    }

    private async Task<string> QueryAsync(string database, string sql, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "psql",
            [
                "-h", ScratchSockets,
                "-U", options.PostgresUser,
                "-d", database,
                "-tA",
                "-c", sql
            ],
            cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    private bool LogServerTail()
    {
        try
        {
            if (File.Exists(ServerLog))
            {
                var text = File.ReadAllText(ServerLog);
                logger.LogWarning(
                    "Deep verify failed; scratch server log tail: {Log}",
                    text.Length > 2000 ? text[^2000..] : text);
            }
        }
        catch (IOException)
        {
            // Log tail is best-effort diagnostics only.
        }
        return false;
    }

    private void DeleteScratch()
    {
        if (Directory.Exists(ScratchRoot))
            Directory.Delete(ScratchRoot, recursive: true);
    }
}
