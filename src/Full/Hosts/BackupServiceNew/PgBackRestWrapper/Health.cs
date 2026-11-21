namespace BackupServiceNew.PgBackRestWrapper;

public sealed partial class PgBackRestClient
{
    /// <summary>A single observation, with no polling, alert transport or scheduling.</summary>
    /// <summary>Collects a single health snapshot from repository, storage, and optionally PostgreSQL archiver statistics.</summary>
    /// <param name="queryPostgres">Whether to query PostgreSQL for archiver status.</param>
    /// <param name="cancellationToken">Token used to cancel PostgreSQL queries.</param>
    public async Task<HealthReport> GetHealthAsync(bool queryPostgres = true, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>(); RepositoryInfo? repository = null; string? archiver = null;
        try
        {
            repository = await GetRepositoryAsync(cancellationToken);
            if (repository.StatusCode != 0) problems.Add(repository.StatusMessage);
            var latest = repository.Backups.Where(b => !b.HasError).OrderByDescending(b => b.Finished).FirstOrDefault();
            if (latest == null || DateTimeOffset.UtcNow - latest.Finished > options.MaximumBackupAge) problems.Add("No recent successful backup.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"Repository: {ex.Message}"); }
        var ready = 0; long repositoryFree = 0, dataFree = 0;
        try
        {
            var status = Path.Combine(options.DataPath, "pg_wal", "archive_status");
            if (Directory.Exists(status)) ready = Directory.EnumerateFiles(status, "*.ready").Count();
            else problems.Add("WAL archive_status directory unavailable.");
            if (ready > options.MaximumWalBacklog) problems.Add("WAL archive backlog exceeds threshold.");
            repositoryFree = Paths.FreeBytes(options.RepositoryPath); dataFree = Paths.FreeBytes(options.DataPath);
            if (repositoryFree < options.MinimumFreeBytes || dataFree < options.MinimumFreeBytes) problems.Add("Storage free capacity is below threshold.");
        }
        catch (IOException ex) { problems.Add($"Storage: {ex.Message}"); }
        if (queryPostgres)
        {
            try
            {
                var result = await Sql("SELECT json_build_object('archived_count',archived_count,'last_archived_wal',last_archived_wal,'last_archived_time',last_archived_time,'failed_count',failed_count,'last_failed_wal',last_failed_wal,'last_failed_time',last_failed_time,'stats_reset',stats_reset, 'failure_unresolved', COALESCE(last_failed_time > last_archived_time, failed_count > 0), 'archive_mode',current_setting('archive_mode'), 'archive_timeout',current_setting('archive_timeout')) FROM pg_stat_archiver;",
                    "postgres", options.SocketPath, options.Port, cancellationToken);
                archiver = result.Output.Trim();
                using var json = System.Text.Json.JsonDocument.Parse(archiver);
                if (json.RootElement.GetProperty("failure_unresolved").GetBoolean()) problems.Add("Latest archive failure has no subsequent archival success.");
                if (json.RootElement.GetProperty("archive_mode").GetString() == "off") problems.Add("PostgreSQL archive_mode is off.");
                if (json.RootElement.GetProperty("archive_timeout").GetString() == "0") problems.Add("PostgreSQL archive_timeout is disabled; low activity may delay archival.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"PostgreSQL archiver: {ex.Message}"); }
        }
        var report = new HealthReport(DateTimeOffset.UtcNow, problems.Count == 0, repository, ready, repositoryFree, dataFree, archiver, problems);
        await reports.WriteAsync("health.json", report); return report;
    }
}
