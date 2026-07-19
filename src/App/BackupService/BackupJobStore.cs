using System.Collections.Concurrent;
using System.Text.Json;

namespace BackupService;

internal sealed class BackupJobStore(BackupServiceOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, BackupJobRecord> _jobs = new();

    public string Root => Path.GetFullPath(options.Directory);
    public string Archives => Path.Combine(Root, "archives");
    public string Jobs => Path.Combine(Root, "jobs");
    public string Staging => Path.Combine(Root, ".staging");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Archives);
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Staging);

        var probe = Path.Combine(Root, $".write-probe-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(probe, "ok", cancellationToken);
        File.Delete(probe);

        foreach (var path in Directory.EnumerateFiles(Jobs, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<BackupJobRecord>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (record is null)
                    continue;
                if (record.Status is "queued" or "running")
                {
                    record = record with
                    {
                        Status = "failed",
                        ErrorMessage = "Backup service restarted before the job completed.",
                        CompletedAt = DateTimeOffset.UtcNow
                    };
                    await SaveFileAsync(record, cancellationToken);
                }
                _jobs[record.JobId] = record;
            }
            catch (Exception)
            {
                // A malformed job record must not prevent access to valid backup archives.
            }
        }
    }

    public IReadOnlyList<BackupJobRecord> List()
        => _jobs.Values.OrderByDescending(x => x.CreatedAt).ToArray();

    public BackupJobRecord? Get(Guid id) => _jobs.GetValueOrDefault(id);

    public BackupJobRecord? FindByIdempotencyKey(string key)
        => _jobs.Values
            .Where(x => string.Equals(x.IdempotencyKey, key, StringComparison.Ordinal))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();

    public async Task SaveAsync(BackupJobRecord record, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveFileAsync(record, cancellationToken);
            _jobs[record.JobId] = record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ResolveArchive(string archivePath)
    {
        var candidate = Path.IsPathRooted(archivePath)
            ? Path.GetFullPath(archivePath)
            : Path.GetFullPath(Path.Combine(Archives, archivePath));
        var prefix = Archives.EndsWith(Path.DirectorySeparatorChar) ? Archives : Archives + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(candidate))
            throw new ArgumentException("Archive does not exist beneath the configured backup root.", nameof(archivePath));
        return candidate;
    }

    private async Task SaveFileAsync(BackupJobRecord record, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(Jobs, $"{record.JobId:N}.json");
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, JsonOptions), cancellationToken);
        File.Move(temporary, destination, true);
    }
}
