using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebAPI.Features.Backups.Models;

namespace WebAPI.Features.Backups;

public sealed class BackupJobService(
    IOptions<BackupOptions> options,
    IConfiguration configuration,
    ILogger<BackupJobService> logger)
{
    private readonly ConcurrentDictionary<Guid, BackupJobRecord> _jobs = new();

    public IReadOnlyList<BackupJobResponse> ListJobs()
        => _jobs.Values
            .OrderByDescending(x => x.CreatedAt)
            .Select(ToResponse)
            .ToArray();

    public BackupJobResponse? GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var job) ? ToResponse(job) : null;

    public BackupJobResponse StartBackup(string? requestedName)
    {
        Directory.CreateDirectory(options.Value.Directory);

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
                var output = await RunToolAsync(["create", "--output", options.Value.Directory, "--name", name]);
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
                    root.TryGetProperty("schemaVersion", out var version) ? version.GetInt32() : 0));
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
            await RunToolAsync(["verify", "--archive", archivePath]);
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
        var command = BuildCommandString(["restore", "--archive", archivePath, "--force"]);
        return new RestorePlanResponse(verify.Success, command, verify.ErrorMessage);
    }

    private void Update(Guid jobId, BackupJobRecord record)
        => _jobs[jobId] = record;

    private async Task<string> RunToolAsync(IReadOnlyList<string> commandArguments)
    {
        var args = BuildArguments(commandArguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = BuildEffectiveOptions().ToolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);

        AddToolEnvironment(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Backup tool failed to start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        return stdout;
    }

    private IReadOnlyList<string> BuildArguments(IReadOnlyList<string> commandArguments)
    {
        var effective = BuildEffectiveOptions();
        var args = SplitArguments(effective.ToolBaseArguments);
        args.AddRange(commandArguments);
        args.AddRange(PostgresArguments(effective));
        args.AddRange(OpenBaoArguments(effective));
        return args;
    }

    private string BuildCommandString(IReadOnlyList<string> commandArguments)
    {
        var effective = BuildEffectiveOptions();
        return string.Join(' ', new[] { effective.ToolPath }.Concat(BuildArguments(commandArguments)).Select(Quote));
    }

    private IEnumerable<string> PostgresArguments(EffectiveBackupOptions effective)
    {
        yield return "--postgres-host";
        yield return effective.PostgresHost;
        yield return "--postgres-port";
        yield return effective.PostgresPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--postgres-user";
        yield return effective.PostgresUser;
        if (!string.IsNullOrWhiteSpace(effective.PostgresBinDir))
        {
            yield return "--postgres-bin-dir";
            yield return effective.PostgresBinDir!;
        }
    }

    private IEnumerable<string> OpenBaoArguments(EffectiveBackupOptions effective)
    {
        yield return "--openbao-address";
        yield return effective.OpenBaoAddress;
        yield return "--openbao-kv-mount";
        yield return effective.OpenBaoKvMount;
    }

    private void AddToolEnvironment(ProcessStartInfo startInfo)
    {
        var effective = BuildEffectiveOptions();
        if (!string.IsNullOrWhiteSpace(effective.PostgresPassword))
            startInfo.Environment["POSTGRES_PASSWORD"] = effective.PostgresPassword;
        if (!string.IsNullOrWhiteSpace(effective.OpenBaoToken))
            startInfo.Environment["OPENBAO_TOKEN"] = effective.OpenBaoToken;
    }

    private EffectiveBackupOptions BuildEffectiveOptions()
    {
        var configuredConnection = configuration.GetConnectionString("froststreamdb");
        var connectionParts = ParseConnectionString(configuredConnection);
        var backupSection = configuration.GetSection(BackupOptions.SectionName);

        return new EffectiveBackupOptions(
            options.Value.ToolPath,
            options.Value.ToolBaseArguments,
            backupSection["PostgresHost"] ?? GetPart(connectionParts, "Host", "Server") ?? options.Value.PostgresHost,
            int.TryParse(backupSection["PostgresPort"] ?? GetPart(connectionParts, "Port"), out var port) ? port : options.Value.PostgresPort,
            backupSection["PostgresUser"] ?? GetPart(connectionParts, "Username", "User ID", "User") ?? options.Value.PostgresUser,
            backupSection["PostgresPassword"] ?? GetPart(connectionParts, "Password") ?? options.Value.PostgresPassword,
            options.Value.PostgresBinDir,
            options.Value.OpenBaoAddress,
            options.Value.OpenBaoToken,
            options.Value.OpenBaoKvMount);
    }

    private static Dictionary<string, string> ParseConnectionString(string? connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connectionString))
            return result;

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2)
                result[parts[0].Trim()] = parts[1].Trim();
        }

        return result;
    }

    private static string? GetPart(IReadOnlyDictionary<string, string> parts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static BackupJobResponse ToResponse(BackupJobRecord record)
        => new(record.JobId, record.Status, record.ArchivePath, record.ErrorMessage, record.CreatedAt, record.CompletedAt);

    private static List<string> SplitArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string Quote(string value)
        => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    private sealed record BackupJobRecord(
        Guid JobId,
        string Status,
        string? ArchivePath,
        string? ErrorMessage,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);

    private sealed record EffectiveBackupOptions(
        string ToolPath,
        string ToolBaseArguments,
        string PostgresHost,
        int PostgresPort,
        string PostgresUser,
        string? PostgresPassword,
        string? PostgresBinDir,
        string OpenBaoAddress,
        string? OpenBaoToken,
        string OpenBaoKvMount);
}
