using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Shared.Backups;
using Shared.Messaging;

namespace BackupService;

internal sealed class BackupCoordinator(
    BackupJobStore store,
    BackupArchiveCatalog catalog,
    IOptions<BackupServiceOptions> options,
    IConfiguration configuration,
    IBackgroundRunReporter runReporter,
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
        CancellationToken cancellationToken,
        string? scheduleKey = null)
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
            ScheduleKey = scheduleKey,
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

        // Reported here rather than in ScheduledBackupConsumer because this is where both paths
        // converge: an admin-triggered backup arrives over HTTP and never touches that consumer.
        var idempotencyKey = record.IdempotencyKey ?? record.JobId.ToString("N");
        var detail = $"{record.Mode} · {record.Name}";
        var taskType = record.Scheduled
            ? record.Mode switch
            {
                "full" => "backup-full",
                "snapshot" => "backup-snapshot",
                _ => "backup"
            }
            : "backup";
        await using var run = record.Scheduled
            ? await runReporter.BeginScheduledAsync(
                taskType, record.ScheduleKey ?? taskType, idempotencyKey, detail, cancellationToken)
            : await runReporter.BeginManualAsync(taskType, idempotencyKey, detail, cancellationToken);

        try
        {
            Directory.CreateDirectory(stagingRoot);
            await run.ReportAsync($"Creating {record.Mode} archive '{record.Name}'…");
            var engine = new BackupEngine(options.Value, configuration);
            var stagedArchive = await engine.CreateAsync(stagingRoot, record.Name, record.Mode, cancellationToken);
            var destination = Path.Combine(store.Archives, Path.GetFileName(stagedArchive));
            if (Directory.Exists(destination))
                throw new IOException($"Backup archive already exists: {destination}");
            await run.ReportAsync("Moving the staged archive into place…");
            Directory.Move(stagedArchive, destination);

            record = record with
            {
                Status = "completed",
                ArchivePath = destination,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(record, cancellationToken);
            if (record.Scheduled && record.Mode == "snapshot")
            {
                await run.ReportAsync("Pruning older scheduled snapshots…");
                await PruneScheduledSnapshotsAsync(cancellationToken);
            }
            run.Succeed($"Archive written to {destination}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Backup job {JobId} failed.", record.JobId);
            run.Fail(ex.Message);
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

    public async Task<RestorePlanDto> BuildRestorePlanAsync(
        string archivePath,
        IReadOnlyDictionary<string, string?>? requestedOptions,
        CancellationToken cancellationToken)
    {
        var path = store.ResolveArchive(archivePath);
        var verify = await VerifyAsync(path, cancellationToken);
        var mode = ReadMode(path);
        var options = CreateRestoreOptions(mode, requestedOptions);
        var arguments = new List<string> { "restore", "--archive", path, "--force" };
        AddOption(arguments, options, "pgdata");
        AddOption(arguments, options, "pg-ctl");

        var targetKey = new[] { "target-time", "target-lsn", "target-name" }
            .FirstOrDefault(key => !string.IsNullOrWhiteSpace(Value(options, key)));
        if (targetKey is not null)
        {
            AddOption(arguments, options, targetKey);
            AddOption(arguments, options, "archive-dir");
            AddOption(arguments, options, "tool-command");
        }
        else if (IsTrue(Value(options, "recover-latest")))
        {
            arguments.Add("--recover-latest");
            AddOption(arguments, options, "archive-dir");
            AddOption(arguments, options, "tool-command");
        }

        var command = "docker compose run --rm --entrypoint dotnet backupservice "
                      + "/app/BackupService.dll "
                      + string.Join(' ', arguments.Select(Quote));
        var explanation = mode switch
        {
            "full" => "Stop FrostStream and PostgreSQL before restoring. This rebuilds the PostgreSQL data directory from the physical base backup.\n\nFor point-in-time recovery: make the WAL archive available inside the backupservice container at the path you enter as WAL archive directory. Most people should then enter the date and time they want to restore to, in UTC (for example, 2026-07-31 12:00:00+00 means July 31, 2026 at noon UTC). Choose exactly one recovery target. The generated command adds --archive-dir and the selected target. The archive must contain the WAL generated after this full backup; without it, recovery can only restore the base backup.",
            "walarchive" or "wal-archive" => "A WAL archive is not a standalone restore source. Restore a matching full backup, make this WAL archive available inside the backupservice container, and enter its mounted directory. Most people should choose the date and time they want to restore to, in UTC. Choose exactly one recovery target. The generated full-restore command will add --archive-dir and that target.",
            _ => "Stop FrostStream services before restoring. This restores each PostgreSQL database and the OpenBao secrets from the logical snapshot. Media files and rebuildable indexes are not included."
        };
        return new RestorePlanDto(verify.Success, explanation, command, options, verify.ErrorMessage);
    }

    private static IReadOnlyList<RestorePlanOptionDto> CreateRestoreOptions(
        string mode,
        IReadOnlyDictionary<string, string?>? requested)
    {
        if (mode is not ("full" or "walarchive" or "wal-archive"))
            return [];

        string Get(string key) => requested?.TryGetValue(key, out var value) == true ? value ?? string.Empty : string.Empty;
        return
        [
            new("pgdata", "PostgreSQL data directory", "The empty data directory that will be rebuilt. Required for a full restore.", "text", Get("pgdata"), "<PGDATA>", true),
            new("pg-ctl", "pg_ctl path (optional)", "Leave blank to use pg_ctl from PATH.", "text", Get("pg-ctl"), "pg_ctl", false),
            new("archive-dir", "WAL archive directory", "Path inside the backupservice container containing the archived WAL segments. Required for point-in-time recovery.", "text", Get("archive-dir"), "<WAL_ARCHIVE_DIR>", false),
            new("target-time", "Restore to a date and time (optional)", "Use this when you want the database restored to how it looked at a particular moment. Enter the time in UTC, for example 2026-07-31 12:00:00+00 means July 31, 2026 at noon UTC. Choose only one recovery target.", "text", Get("target-time"), "2026-07-31 12:00:00+00", false),
            new("target-lsn", "Recovery target LSN", "A PostgreSQL LSN. Use only one recovery target.", "text", Get("target-lsn"), "0/1", false),
            new("target-name", "Recovery target name", "A named PostgreSQL recovery target. Use only one recovery target.", "text", Get("target-name"), "target name", false),
            new("recover-latest", "Recover latest WAL", "Replay all available WAL instead of choosing a time, LSN, or name.", "checkbox", IsTrue(Get("recover-latest")) ? "true" : "false", null, false),
            new("tool-command", "BackupService command (optional)", "Command used by PostgreSQL to retrieve archived WAL during recovery.", "text", Get("tool-command"), "dotnet /opt/froststream/BackupService.dll", false)
        ];
    }

    private static string? Value(IReadOnlyList<RestorePlanOptionDto> options, string key)
        => options.FirstOrDefault(x => x.Key == key)?.Value;

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static void AddOption(List<string> arguments, IReadOnlyList<RestorePlanOptionDto> options, string key)
    {
        var option = options.FirstOrDefault(x => x.Key == key);
        if (option is null)
            return;
        var value = string.IsNullOrWhiteSpace(option.Value) ? option.Placeholder : option.Value;
        if (!string.IsNullOrWhiteSpace(value))
            arguments.AddRange(["--" + key, value]);
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
