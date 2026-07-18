using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Shared.Backups;

namespace BackupService;

internal sealed class BackupCoordinator(
    BackupJobStore store,
    BackupArchiveCatalog catalog,
    IOptions<BackupServiceOptions> options,
    IConfiguration configuration,
    ILogger<BackupCoordinator> logger) : BackgroundService
{
    private static readonly HashSet<string> Modes = ["snapshot", "full", "wal-archive"];
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BackupJobRecord>> _completion = new();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public async Task<BackupJobRecord> QueueAsync(
        string? requestedName,
        string? requestedMode,
        bool scheduled,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey)
            && store.FindByIdempotencyKey(idempotencyKey) is { Status: not "failed" } existing)
            return existing;

        var mode = string.IsNullOrWhiteSpace(requestedMode) ? "snapshot" : requestedMode.Trim().ToLowerInvariant();
        if (!Modes.Contains(mode))
            throw new ArgumentException($"Unsupported backup mode '{requestedMode}'.", nameof(requestedMode));

        var id = Guid.NewGuid();
        var name = SanitizeName(string.IsNullOrWhiteSpace(requestedName)
            ? $"froststream-{mode}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : requestedName);
        var record = new BackupJobRecord
        {
            JobId = id,
            Status = "queued",
            Name = name,
            Mode = mode,
            Scheduled = scheduled,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(record, cancellationToken);
        _completion[id] = new TaskCompletionSource<BackupJobRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(id, cancellationToken);
        return record;
    }

    public async Task<BackupJobRecord> WaitAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (_completion.TryGetValue(jobId, out var pending))
            return await pending.Task.WaitAsync(cancellationToken);
        return store.Get(jobId) ?? throw new KeyNotFoundException($"Backup job {jobId} was not found.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (store.Get(jobId) is { } record)
                await RunAsync(record, stoppingToken);
        }
    }

    private async Task RunAsync(BackupJobRecord record, CancellationToken cancellationToken)
    {
        record = record with { Status = "running" };
        await store.SaveAsync(record, cancellationToken);
        var stagingRoot = Path.Combine(store.Staging, record.JobId.ToString("N"));

        try
        {
            Directory.CreateDirectory(stagingRoot);
            var engine = new BackupEngine(options.Value, configuration);
            var stagedArchive = await engine.CreateAsync(stagingRoot, record.Name, record.Mode, cancellationToken);
            var destination = Path.Combine(store.Archives, Path.GetFileName(stagedArchive));
            if (Directory.Exists(destination))
                throw new IOException($"Backup archive already exists: {destination}");
            Directory.Move(stagedArchive, destination);

            record = record with
            {
                Status = "completed",
                ArchivePath = destination,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(record, cancellationToken);
            if (record.Scheduled && record.Mode == "snapshot")
                await PruneScheduledSnapshotsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Backup job {JobId} failed.", record.JobId);
            record = record with
            {
                Status = "failed",
                ErrorMessage = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(record, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            if (_completion.TryRemove(record.JobId, out var completion))
                completion.TrySetResult(record);
        }
    }

    internal async Task PruneScheduledSnapshotsAsync(CancellationToken cancellationToken)
    {
        var keep = Math.Max(1, options.Value.ScheduledRetentionCount);
        var expired = store.List()
            .Where(x => x.Scheduled && x.Status == "completed" && x.Mode == "snapshot" && x.ArchivePath is not null)
            .OrderByDescending(x => x.CompletedAt)
            .Skip(keep)
            .ToArray();
        foreach (var job in expired)
        {
            var path = store.ResolveArchive(job.ArchivePath!);
            Directory.Delete(path, recursive: true);
            await store.SaveAsync(job with { ArchivePath = null }, cancellationToken);
        }
    }

    public async Task<VerifyBackupDto> VerifyAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            var path = store.ResolveArchive(archivePath);
            await new BackupEngine(options.Value, configuration).VerifyAsync(path, cancellationToken);
            return new VerifyBackupDto(true, null);
        }
        catch (Exception ex)
        {
            return new VerifyBackupDto(false, ex.Message);
        }
    }

    public async Task<RestorePlanDto> BuildRestorePlanAsync(string archivePath, CancellationToken cancellationToken)
    {
        var path = store.ResolveArchive(archivePath);
        var verify = await VerifyAsync(path, cancellationToken);
        var mode = ReadMode(path);
        IReadOnlyList<string> arguments = mode is "full" or "walarchive" or "wal-archive"
            ? ["restore", "--archive", path, "--force", "--pgdata", "<PGDATA>", "--pg-ctl", "<pg_ctl>", "--target-time", "<YYYY-MM-DD HH:MM:SS+00>"]
            : ["restore", "--archive", path, "--force"];
        var command = "docker compose run --rm --entrypoint dotnet backupservice "
                      + "/app/BackupService.dll "
                      + string.Join(' ', arguments.Select(Quote));
        return new RestorePlanDto(verify.Success, command, verify.ErrorMessage);
    }

    public IReadOnlyList<BackupArchiveDto> ListArchives() => catalog.List();

    private static string ReadMode(string archive)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive, "manifest.json")));
        return document.RootElement.TryGetProperty("mode", out var value)
            ? (value.GetString() ?? "snapshot").ToLowerInvariant()
            : "snapshot";
    }

    private static string SanitizeName(string value)
    {
        var safe = new string(value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }

    private static string Quote(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
}
