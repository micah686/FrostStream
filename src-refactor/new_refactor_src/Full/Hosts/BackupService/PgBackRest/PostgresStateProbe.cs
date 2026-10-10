namespace BackupService.PgBackRest;

internal sealed record PostgresState(bool ServerRunning, bool StalePidFile)
{
    public bool SafeToRestore => !ServerRunning;
}

/// <summary>
/// Restore-wizard prerequisite probe: is the live postgres server running? Detection uses
/// pg_isready against the shared socket volume; a postmaster.pid left in the shared PGDATA
/// while the server is not reachable is reported as stale (a crashed or force-removed
/// container). pgBackRest's own postmaster.pid refusal remains the hard backstop.
/// </summary>
internal class PostgresStateProbe(BackupServiceOptions options)
{
    public virtual async Task<PostgresState> ProbeAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "pg_isready",
            [
                "-h", options.PostgresSocketDirectory,
                "-p", options.PostgresPort.ToString(),
                "-U", options.PostgresUser,
                "-t", "3"
            ],
            throwOnError: false,
            cancellationToken: cancellationToken);

        // pg_isready: 0 = accepting, 1 = rejecting (starting up), 2 = no response. 0 and 1 both
        // mean a postmaster is alive — never restore over it.
        var running = result.ExitCode is 0 or 1;
        var pidFile = File.Exists(Path.Combine(options.PgDataPath, "postmaster.pid"));
        return new PostgresState(running, StalePidFile: !running && pidFile);
    }
}
