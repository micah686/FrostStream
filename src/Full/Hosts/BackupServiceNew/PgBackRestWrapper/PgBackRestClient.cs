using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PgBackRestWrapper;

/// <summary>On-demand container-local pgBackRest operations. No scheduler, worker, HTTP service or PostgreSQL driver dependency.</summary>
public sealed partial class PgBackRestClient
{
    private readonly WrapperOptions options;
    private readonly ICommandRunner runner;
    private readonly ReportStore reports;
    private readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>Creates a client using validated wrapper options and an optional command runner.</summary>
    /// <param name="options">Paths, executable names, and operational settings for pgBackRest and PostgreSQL.</param>
    /// <param name="runner">Optional command runner; when omitted, a process based runner is used.</param>
    public PgBackRestClient(WrapperOptions options, ICommandRunner? runner = null)
    {
        options.Validate(); this.options = options; this.runner = runner ?? new ProcessRunner();
        reports = new ReportStore(options.StatePath);
    }
    public static IReadOnlyList<BackupType> SupportedBackupTypes { get; } = Array.AsReadOnly(Enum.GetValues<BackupType>());

    /// <summary>Explicitly writes managed configuration; never overwrites an existing file.</summary>
    /// <summary>Writes generated configuration to its configured path without replacing an existing file.</summary>
    /// <param name="cancellationToken">Token used to cancel the asynchronous write.</param>
    public async Task WriteConfigurationAsync(CancellationToken cancellationToken = default)
    {
        Paths.NoLinks(options.ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ConfigPath)!);
        await using var stream = new FileStream(options.ConfigPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(RenderConfiguration().AsMemory(), cancellationToken);
    }
    /// <summary>Builds pgBackRest configuration text from the current options.</summary>
    public string RenderConfiguration() => $"""
        [global]
        repo1-path={options.RepositoryPath}
        repo1-retention-full={options.FullRetention}
        repo1-retention-full-type=count
        repo1-retention-diff={options.DifferentialRetention}
        compress-type=zst
        compress-level={options.CompressionLevel}
        process-max={options.ProcessCount}
        repo1-bundle={(options.Bundle ? "y" : "n")}
        repo1-block={(options.BlockIncremental ? "y" : "n")}
        archive-check=y
        archive-copy=y
        start-fast=y
        log-level-console=info
        log-level-file=off
        lock-path={Path.Combine(options.StatePath, "pgbackrest-lock")}
        
        [{options.Stanza}]
        pg1-path={options.DataPath}
        pg1-socket-path={options.SocketPath}
        pg1-port={options.Port}
        pg1-user={options.DatabaseUser}
        
        """;

    /// <summary>Builds common pgBackRest arguments for a command and its additional arguments.</summary>
    /// <param name="command">pgBackRest command name.</param><param name="extra">Additional command arguments.</param>
    private List<string> Arguments(string command, params string[] extra) =>
        [$"--config={options.ConfigPath}", $"--stanza={options.Stanza}",
            ..(command is "archive-push" or "stanza-create" or "check" ? Array.Empty<string>() : new[] { "--repo=1" }), ..extra, command];
    /// <summary>Runs a command, records its result, and optionally requires successful completion.</summary>
    /// <param name="executable">Executable path or name.</param><param name="args">Arguments passed without shell expansion.</param>
    /// <param name="ct">Cancellation token.</param><param name="progress">Optional streamed output receiver.</param>
    /// <param name="requireSuccess">Whether unsuccessful results should throw.</param><param name="timeout">Optional timeout override.</param>
    private async Task<CommandResult> Run(string executable, IReadOnlyList<string> args,
        CancellationToken ct, IProgress<OutputLine>? progress = null, bool requireSuccess = true, TimeSpan? timeout = null)
    {
        var spec = new CommandSpec(executable, args, timeout ?? options.CommandTimeout);
        CommandResult result;
        try { result = await runner.RunAsync(spec, progress, ct); }
        catch (Exception ex)
        {
            await reports.WriteAsync($"failure-{Guid.NewGuid():N}.json", new { Command = spec, At = DateTimeOffset.UtcNow, Error = ex.ToString() });
            throw;
        }
        await reports.WriteAsync($"command-{result.Id:N}.json", result);
        if (requireSuccess && !result.Success) throw new CommandFailedException(result);
        return result;
    }
    /// <summary>Runs a pgBackRest command with the wrapper's common configuration arguments.</summary>
    /// <param name="command">pgBackRest command name.</param><param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional streamed output receiver.</param><param name="extra">Additional command arguments.</param>
    private Task<CommandResult> BackRest(string command, CancellationToken ct, IProgress<OutputLine>? progress = null,
        params string[] extra) => Run(options.PgBackRest, Arguments(command, extra), ct, progress);
    /// <summary>Serializes an operation and holds the cross-process state lock while it runs.</summary>
    /// <param name="action">Asynchronous operation to execute under the lock.</param><param name="ct">Cancellation token.</param>
    private async Task<T> Exclusive<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Paths.Writable(options.StatePath);
            // Held for the entire operation. Archive commands deliberately do not use this lock:
            // an online backup waits for PostgreSQL's archive_command.
            await using var lease = new FileStream(Path.Combine(options.StatePath, "operation.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return await action();
        }
        finally { gate.Release(); }
    }
    /// <summary>Checks installed tool versions and required pgBackRest commands.</summary>
    /// <param name="cancellationToken">Token used to cancel the checks.</param>
    public Task<CompatibilityReport> CheckCompatibilityAsync(CancellationToken cancellationToken = default) => Compatibility(cancellationToken);
    /// <summary>Checks tool versions and command support, then saves the compatibility report.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <summary>Checks tool versions and required command support, then saves the resulting report.</summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task<CompatibilityReport> Compatibility(CancellationToken ct)
    {
        var br = await Run(options.PgBackRest, ["version"], ct);
        var pg = await Run(options.Postgres, ["--version"], ct);
        var ctl = await Run(options.PgCtl, ["--version"], ct);
        var psql = await Run(options.Psql, ["--version"], ct);
        if (Major(pg.Output) < 14) throw new NotSupportedException("PostgreSQL 14 or newer is required.");
        if (Major(pg.Output) != Major(ctl.Output) || Major(pg.Output) != Major(psql.Output))
            throw new InvalidOperationException("postgres, pg_ctl and psql major versions must match.");
        foreach (var command in new[] { "backup", "restore", "info", "check", "stanza-create", "expire", "archive-push", "archive-get" })
            await Run(options.PgBackRest, ["help", command], ct);
        var verify = await Run(options.PgBackRest, ["help", "verify"], ct, requireSuccess: false);
        var set = verify.Success ? await Run(options.PgBackRest, ["help", "verify", "set"], ct, requireSuccess: false) : null;
        var report = new CompatibilityReport(br.Output.Trim(), pg.Output.Trim(), ctl.Output.Trim(), psql.Output.Trim(), verify.Success, set?.Success == true);
        await reports.WriteAsync("compatibility.json", report);
        return report;
    }
    /// <summary>Extracts the first major version number from tool version text.</summary>
    /// <param name="version">Version text reported by a PostgreSQL tool.</param>
    private static int Major(string version)
    {
        var match = Regex.Match(version, @"\b(\d+)(?:\.\d+)*\b");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : throw new FormatException("Cannot identify PostgreSQL major version.");
    }
    /// <summary>Creates the stanza and runs an initial repository check after validating compatibility.</summary>
    /// <param name="cancellationToken">Token used to cancel bootstrap operations.</param>
    public Task<CommandResult> BootstrapAsync(CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        var compatibility = await Compatibility(cancellationToken);
        if (!compatibility.VerifyAvailable) throw new NotSupportedException("The installed pgBackRest must support verify.");
        Paths.Writable(options.RepositoryPath); Paths.Writable(options.DataPath);
        await BackRest("stanza-create", cancellationToken);
        return await BackRest("check", cancellationToken);
    }, cancellationToken);
    /// <summary>Runs pgBackRest's stanza and archive check.</summary>
    /// <param name="cancellationToken">Token used to cancel the check.</param>
    public Task<CommandResult> CheckAsync(CancellationToken cancellationToken = default) => BackRest("check", cancellationToken);
    /// <summary>Expires backups according to the configured retention policy.</summary>
    /// <param name="cancellationToken">Token used to cancel expiration.</param>
    public Task<CommandResult> ExpireAsync(CancellationToken cancellationToken = default) =>
        Exclusive(() => BackRest("expire", cancellationToken), cancellationToken);

    /// <summary>Reads backup inventory and status for the configured stanza.</summary>
    /// <param name="cancellationToken">Token used to cancel the inventory request.</param>
    public async Task<RepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await BackRest("info", cancellationToken, null, "--output=json", "--log-level-console=off");
        using var json = JsonDocument.Parse(result.Output);
        var stanza = json.RootElement.EnumerateArray().Single(x => x.GetProperty("name").GetString() == options.Stanza);
        var backups = new List<BackupInfo>();
        if (stanza.TryGetProperty("backup", out var array)) foreach (var b in array.EnumerateArray())
        {
            var ts = b.GetProperty("timestamp"); var info = b.GetProperty("info");
            backups.Add(new BackupInfo(b.GetProperty("label").GetString()!, b.GetProperty("type").GetString()!,
                DateTimeOffset.FromUnixTimeSeconds(ts.GetProperty("start").GetInt64()), DateTimeOffset.FromUnixTimeSeconds(ts.GetProperty("stop").GetInt64()),
                info.GetProperty("size").GetInt64(), info.GetProperty("repository").GetProperty("size").GetInt64(),
                b.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True, b.Clone()));
        }
        var status = stanza.GetProperty("status");
        return new RepositoryInfo(DateTimeOffset.UtcNow, status.GetProperty("code").GetInt32(), status.GetProperty("message").GetString()!, backups, stanza.Clone());
    }
    /// <summary>Creates an online backup and returns its repository metadata and command result.</summary>
    /// <param name="type">Requested full, differential, or incremental backup type.</param><param name="name">Optional printable backup annotation.</param>
    /// <param name="progress">Optional streamed command output receiver.</param><param name="cancellationToken">Token used to cancel the operation.</param>
    public Task<BackupResult> BackupAsync(BackupType type, string? name = null, IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (name != null && (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)))
            throw new ArgumentException("Backup names must contain 1–128 printable characters.", nameof(name));
        await Compatibility(cancellationToken);
        var operationId = Guid.NewGuid().ToString("N");
        var args = new List<string> { $"--type={type switch { BackupType.Full => "full", BackupType.Differential => "diff", _ => "incr" }}",
            "--online", "--archive-check", "--compress-type=zst", $"--annotation=wrapper-id={operationId}" };
        if (name != null) args.Add($"--annotation=name={name}");
        var command = await BackRest("backup", cancellationToken, progress, args.ToArray());
        var inventory = await GetRepositoryAsync(cancellationToken);
        var produced = inventory.Backups.SingleOrDefault(b => b.Metadata.TryGetProperty("annotation", out var a) &&
            a.TryGetProperty("wrapper-id", out var id) && id.GetString() == operationId)
            ?? throw new InvalidOperationException("Backup exited successfully but its annotation was not found in repository metadata.");
        if (produced.HasError) throw new InvalidOperationException("Repository reports an error for the produced backup.");
        var result = new BackupResult(type, produced, command);
        await reports.WriteAsync($"backup-{command.Id:N}.json", result);
        return result;
    }, cancellationToken);
    /// <summary>Verifies the repository or one selected backup when supported by the installed version.</summary>
    /// <param name="backupLabel">Optional pgBackRest label restricting verification to one backup.</param><param name="progress">Optional streamed command output receiver.</param>
    /// <param name="cancellationToken">Token used to cancel verification.</param>
    public Task<CommandResult> VerifyAsync(string? backupLabel = null, IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        var capability = await Compatibility(cancellationToken);
        if (!capability.VerifyAvailable || (backupLabel != null && !capability.VerifySetAvailable))
            throw new NotSupportedException("Installed pgBackRest does not support the requested verification scope.");
        var args = new List<string> { "--output=text" };
        if (backupLabel != null) { Label(backupLabel); args.Add($"--set={backupLabel}"); }
        return await BackRest("verify", cancellationToken, progress, args.ToArray());
    }, cancellationToken);
    /// <summary>Validates the syntax of a pgBackRest backup label.</summary>
    /// <param name="label">Backup label to validate.</param>
    private static void Label(string label)
    {
        if (!Regex.IsMatch(label, @"^\d{8}-\d{6}F(?:_\d{8}-\d{6}[DI])?$")) throw new ArgumentException("Invalid pgBackRest backup label.");
    }
    /// <summary>Pushes a WAL file into the configured archive.</summary>
    /// <param name="walPath">Absolute path to the WAL file.</param><param name="cancellationToken">Token used to cancel the command.</param>
    public Task<CommandResult> ArchivePushAsync(string walPath, CancellationToken cancellationToken = default)
    {
        Paths.Absolute(walPath);
        return Run(options.PgBackRest, Arguments("archive-push").Concat([walPath]).ToArray(), cancellationToken);
    }
    /// <summary>Retrieves a WAL segment or timeline history file from the archive.</summary>
    /// <param name="walName">WAL segment or history filename.</param><param name="destination">Absolute output file path.</param>
    /// <param name="cancellationToken">Token used to cancel retrieval.</param>
    public Task<CommandResult> ArchiveGetAsync(string walName, string destination, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(walName, @"^(?:[0-9A-F]{24}(?:\.partial)?|[0-9A-F]{8}\.history)$")) throw new ArgumentException("Invalid WAL name.");
        Paths.Absolute(destination);
        return Run(options.PgBackRest, Arguments("archive-get").Concat([walName, destination]).ToArray(), cancellationToken);
    }
    /// <summary>Runs SQL through psql using the configured database user and recovery timeout.</summary>
    /// <param name="sql">SQL statement to execute.</param><param name="database">Database name.</param><param name="socket">PostgreSQL socket directory.</param>
    /// <param name="port">PostgreSQL port.</param><param name="ct">Cancellation token.</param>
    private Task<CommandResult> Sql(string sql, string database, string socket, int port, CancellationToken ct) =>
        Run(options.Psql, ["-X", "-w", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-h", socket, "-p", port.ToString(CultureInfo.InvariantCulture),
            "-U", options.DatabaseUser, "-d", database, "-c", sql], ct, timeout: options.RecoveryTimeout);
    /// <summary>Creates a PostgreSQL recovery restore point.</summary>
    /// <param name="name">Printable restore point name.</param><param name="cancellationToken">Token used to cancel the SQL command.</param>
    public Task<CommandResult> CreateRestorePointAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 63 || name.Any(char.IsControl)) throw new ArgumentException("Invalid restore point name.");
        return Sql($"SELECT pg_create_restore_point('{name.Replace("'", "''")}');", "postgres", options.SocketPath, options.Port, cancellationToken);
    }
    /// <summary>Exports a database as a custom format dump after checking client and server major versions.</summary>
    /// <param name="database">Database to export.</param><param name="outputFile">Absolute destination file, which must not already exist.</param>
    /// <param name="cancellationToken">Token used to cancel the export.</param>
    public Task<CommandResult> ExportDatabaseAsync(string database, string outputFile, CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        Paths.Absolute(outputFile); Paths.Disjoint(outputFile, options.DataPath); Paths.Disjoint(outputFile, options.RepositoryPath);
        Paths.NoLinks(outputFile);
        await using var reservation = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var temporary = outputFile + $".{Guid.NewGuid():N}.partial";
        try
        {
            var version = await Run(options.PgDump, ["--version"], cancellationToken);
            var server = await Sql("SHOW server_version;", database, options.SocketPath, options.Port, cancellationToken);
            if (Major(version.Output) != Major(server.Output)) throw new InvalidOperationException("pg_dump must match server major version.");
            var result = await Run(options.PgDump, ["-w", "--format=custom", "--host", options.SocketPath, "--port", options.Port.ToString(CultureInfo.InvariantCulture),
                "--username", options.DatabaseUser, "--file", temporary, "--dbname", database], cancellationToken);
            File.Move(temporary, outputFile, true); return result;
        }
        catch { File.Delete(outputFile); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellationToken);
}
