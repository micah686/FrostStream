using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Backups;
using WebAPI.Features.Backups.Models;

namespace WebAPI.Features.Backups;

public sealed class BackupJobService(
    IOptions<BackupOptions> options,
    IConfiguration configuration,
    ILogger<BackupJobService> logger)
{
    private static readonly string[] KnownModes = ["snapshot", "full", "wal-archive"];

    private readonly ConcurrentDictionary<Guid, BackupJobRecord> _jobs = new();
    private readonly BackupToolClient _client = new(options.Value, configuration);

    public IReadOnlyList<BackupJobResponse> ListJobs()
        => _jobs.Values
            .OrderByDescending(x => x.CreatedAt)
            .Select(ToResponse)
            .ToArray();

    public BackupJobResponse? GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var job) ? ToResponse(job) : null;

    public BackupJobResponse StartBackup(string? requestedName, string? requestedMode = null)
    {
        Directory.CreateDirectory(options.Value.Directory);

        var mode = NormalizeMode(requestedMode);
        var jobId = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? jobId.ToString("N")
            : requestedName.Trim();
        var job = new BackupJobRecord(jobId, "queued", null, null, DateTimeOffset.UtcNow, null);
        _jobs[jobId] = job;

        _ = Task.Run(async () =>
        {
            Update(jobId, job with { Status = "running" });
            try
            {
                var output = await _client.RunAsync(["create", "--output", options.Value.Directory, "--name", name, "--mode", mode]);
                var archivePath = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                Update(jobId, job with
                {
                    Status = "completed",
                    ArchivePath = archivePath,
                    CompletedAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup job {JobId} failed.", jobId);
                Update(jobId, job with
                {
                    Status = "failed",
                    ErrorMessage = ex.Message,
                    CompletedAt = DateTimeOffset.UtcNow
                });
            }
        });

        return ToResponse(job);
    }

    public IReadOnlyList<BackupSummaryResponse> ListBackups()
    {
        if (!Directory.Exists(options.Value.Directory))
            return [];

        var results = new List<BackupSummaryResponse>();
        foreach (var manifestPath in Directory.EnumerateFiles(options.Value.Directory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;
                results.Add(new BackupSummaryResponse(
                    Path.GetDirectoryName(manifestPath) ?? options.Value.Directory,
                    root.TryGetProperty("createdAtUtc", out var created) && created.TryGetDateTimeOffset(out var createdAt) ? createdAt : null,
                    root.TryGetProperty("mediaIncluded", out var mediaIncluded) && mediaIncluded.GetBoolean(),
                    root.TryGetProperty("schemaVersion", out var version) ? version.GetInt32() : 0,
                    root.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "Snapshot" : "Snapshot"));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping invalid backup manifest {ManifestPath}.", manifestPath);
            }
        }

        return results.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<VerifyBackupResponse> VerifyAsync(string archivePath)
    {
        try
        {
            await _client.RunAsync(["verify", "--archive", archivePath]);
            return new VerifyBackupResponse(true, null);
        }
        catch (Exception ex)
        {
            return new VerifyBackupResponse(false, ex.Message);
        }
    }

    public async Task<RestorePlanResponse> BuildRestorePlanAsync(string archivePath)
    {
        var verify = await VerifyAsync(archivePath);
        var command = _client.BuildCommandString(BuildRestoreArguments(archivePath));
        return new RestorePlanResponse(verify.Success, command, verify.ErrorMessage);
    }

    /// <summary>
    /// Restore arguments differ by backup mode. Snapshot restores run against a live server;
    /// full/PITR restores rebuild a data directory offline and need operator-supplied placeholders.
    /// </summary>
    private IReadOnlyList<string> BuildRestoreArguments(string archivePath)
    {
        var mode = ReadManifestMode(archivePath);
        return mode.ToLowerInvariant() switch
        {
            "full" or "walarchive" or "wal-archive" =>
            [
                "restore", "--archive", archivePath, "--force",
                "--pgdata", "<PGDATA>", "--pg-ctl", "<pg_ctl>",
                "--target-time", "<YYYY-MM-DD HH:MM:SS+00>"
            ],
            _ => ["restore", "--archive", archivePath, "--force"]
        };
    }

    private string ReadManifestMode(string archivePath)
    {
        try
        {
            var manifestPath = Path.Combine(archivePath, "manifest.json");
            if (!File.Exists(manifestPath))
                return "Snapshot";
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "Snapshot" : "Snapshot";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read backup mode from manifest at {ArchivePath}.", archivePath);
            return "Snapshot";
        }
    }

    private static string NormalizeMode(string? requestedMode)
    {
        var mode = requestedMode?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(mode) || !KnownModes.Contains(mode) ? "snapshot" : mode;
    }

    private void Update(Guid jobId, BackupJobRecord record)
        => _jobs[jobId] = record;

    private static BackupJobResponse ToResponse(BackupJobRecord record)
        => new(record.JobId, record.Status, record.ArchivePath, record.ErrorMessage, record.CreatedAt, record.CompletedAt);

    private sealed record BackupJobRecord(
        Guid JobId,
        string Status,
        string? ArchivePath,
        string? ErrorMessage,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);
}
