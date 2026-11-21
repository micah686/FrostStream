using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackupServiceNew.PgBackRestWrapper;

namespace BackupServiceNew.Recovery;

internal enum CheckState { Pending, Running, Passed, Failed, Unknown }
internal sealed record RecoveryCheck(string Name, CheckState State, string Detail, string Remediation, DateTimeOffset ObservedAt);
internal sealed record ReadinessReport(IReadOnlyList<RecoveryCheck> Checks, RepositoryInfo? Repository, int? PostgresMajor,
    long FreeBytes, bool Populated)
{
    public bool Ready => Checks.Count > 0 && Checks.All(check => check.State == CheckState.Passed);
}

internal sealed class RecoveryReadiness(WrapperOptions options, PgBackRestClient client, ICommandRunner runner, ILogger<RecoveryReadiness> logger)
{
    private static readonly string[] CheckNames =
    [
        "Destination mount and permissions", "PostgreSQL process markers", "PostgreSQL socket probe",
        "Destination free space", "Recovery tools", "Repository access and metadata", "Backups available"
    ];

    public static ReadinessReport Pending() => new(CheckNames.Select(name =>
        new RecoveryCheck(name, CheckState.Pending, "Waiting for checks to start.", "", DateTimeOffset.UtcNow)).ToArray(),
        null, null, 0, false);

    public async Task<ReadinessReport> CheckLogicalAsync(LogicalBackups logical, CancellationToken ct)
    {
        var checks = new List<RecoveryCheck>();
        await Inspect("PostgreSQL is running", () => logical.ProbeRestoreAsync(ct), "Start PostgreSQL; stop application writers and verify database credentials.");
        await Inspect("PID and socket evidence", () =>
        {
            if (!File.Exists(Path.Combine(options.DataPath, "postmaster.pid"))) throw new InvalidOperationException("The shared PGDATA has no PostgreSQL PID file.");
            if (!Directory.Exists(options.SocketPath)) throw new InvalidOperationException("The PostgreSQL socket directory is unavailable.");
            return Task.CompletedTask;
        }, "Mount the same PostgreSQL data and socket volumes in both containers.");
        await Inspect("Destination mount and permissions", () =>
        {
            if (FindMount(options.DataPath) is null) throw new InvalidOperationException("The PostgreSQL destination is outside a verifiable mounted volume.");
            Paths.NoLinks(options.DataPath);
            return Task.CompletedTask;
        }, "Mount the PostgreSQL data volume in BackupService.");
        await Inspect("Available disk space", () =>
        {
            if (Paths.FreeBytes(options.DataPath) < options.MinimumFreeBytes) throw new InvalidOperationException("Insufficient free space on the PostgreSQL volume.");
            return Task.CompletedTask;
        }, "Free space on the database volume before restoring.");
        await Inspect("Repository access", () =>
        {
            var root = Path.GetDirectoryName(options.RepositoryPath)!;
            Paths.NoLinks(root);
            if (!Directory.Exists(root)) throw new InvalidOperationException("The backup root is unavailable.");
            return Task.CompletedTask;
        }, "Mount the backup root containing logical bundles.");
        await Inspect("Logical backup inventory", () =>
        {
            if (!logical.List().Any(b => b.Mode == "logical")) throw new InvalidOperationException("No logical backups are available.");
            return Task.CompletedTask;
        }, "Mount a completed logical backup bundle.");
        await Inspect("Required tools", async () =>
        {
            foreach (var tool in new[] { "psql", "pg_restore", "createdb", "dropdb" })
                if (!(await RunTool(tool, ["--version"], ct)).Success) throw new InvalidOperationException($"{tool} is unavailable.");
        }, "Install matching PostgreSQL client tools.");
        return new(checks, null, null, 0, true);

        async Task Inspect(string name, Func<Task> action, string remediation)
        {
            try { await action(); checks.Add(new(name, CheckState.Passed, "Check passed.", "", DateTimeOffset.UtcNow)); }
            catch (Exception ex) when (IsCheckError(ex, ct)) { checks.Add(new(name, CheckState.Failed, ex.Message, remediation, DateTimeOffset.UtcNow)); }
        }
    }

    public async Task<ReadinessReport> CheckAsync(CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var mount = FindMount(options.DataPath);
        var populated = false;
        var free = 0L;
        int? postgresMajor = null;
        RepositoryInfo? repository = null;
        var checks = new List<RecoveryCheck>();
        var (socketDirectory, socketProblem) = CheckSocketMount();

        Check(0, "Mount the shared PostgreSQL data volume and give BackupService write access.", () =>
        {
            if (mount is null)
                return (CheckState.Unknown, "The configured data path is outside a verifiable mounted volume.");
            Paths.NoLinks(options.DataPath);
            var path = Directory.Exists(options.DataPath) ? options.DataPath : Path.GetDirectoryName(options.DataPath)!;
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The PGDATA parent is missing.");
            if (File.Exists(options.DataPath)) throw new IOException("The configured data path is a file.");
            // Do not create PGDATA during preflight.
            Paths.Writable(path);
            populated = Directory.Exists(options.DataPath) && Directory.EnumerateFileSystemEntries(options.DataPath).Any();
            return (CheckState.Passed, $"{options.DataPath} is writable on {mount}.");
        });

        Check(1, "Stop PostgreSQL and investigate any PID or socket files on the host before checking again.", () =>
        {
            if (mount is null)
                return (CheckState.Unknown, "The shared PostgreSQL data volume is unavailable.");
            var markers = Directory.Exists(options.DataPath)
                ? Directory.EnumerateFileSystemEntries(options.DataPath, "postmaster.pid").ToList()
                : [];
            if (socketDirectory is not null)
            {
                var socket = $".s.PGSQL.{options.Port}";
                markers.AddRange(Directory.EnumerateFileSystemEntries(socketDirectory)
                    .Where(path => Path.GetFileName(path) is { } name && (name == socket || name == socket + ".lock")));
            }
            if (markers.Count > 0)
                return (CheckState.Failed, $"Investigate {string.Join(", ", markers)}.");
            return socketDirectory is null
                ? (CheckState.Unknown, socketProblem)
                : (CheckState.Passed, "No postmaster PID, socket or socket lock files found on the shared volumes.");
        });

        try
        {
            if (socketDirectory is null)
                Add(2, CheckState.Unknown, socketProblem, "Mount the same PostgreSQL socket volume in both containers.");
            else
            {
                var probe = await RunTool("pg_isready",
                    ["-h", socketDirectory, "-p", options.Port.ToString(CultureInfo.InvariantCulture), "-U", options.DatabaseUser, "-d", "postgres", "-t", "3"], budget.Token);
                var state = probe is { TimedOut: false, Cancelled: false } ? probe.ExitCode switch
                {
                    0 or 1 => CheckState.Failed,
                    2 => CheckState.Passed,
                    _ => CheckState.Unknown
                } : CheckState.Unknown;
                Add(2, state, state switch
                {
                    CheckState.Passed => "The shared socket did not respond. Confirm PostgreSQL is stopped on the host.",
                    CheckState.Failed => "PostgreSQL is responding on the shared socket.",
                    _ => "The socket probe could not establish server status."
                }, "Stop PostgreSQL on the host and check the socket mount and probe.");
            }
        }
        catch (Exception ex) when (IsCheckError(ex, ct))
        {
            LogCheckError(2, ex);
            Add(2, CheckState.Unknown, "The socket probe could not run.", "Check that pg_isready is installed and the shared socket is accessible.");
        }

        Check(3, "Free space on the PostgreSQL volume.", () =>
        {
            if (mount is null)
                return (CheckState.Unknown, "No mounted destination to measure.");
            free = new DriveInfo(mount).AvailableFreeSpace;
            return (free >= options.MinimumFreeBytes ? CheckState.Passed : CheckState.Failed,
                $"{RecoveryConsole.Bytes(free)} free. At least {RecoveryConsole.Bytes(options.MinimumFreeBytes)} is required before selecting a backup.");
        });

        try
        {
            var br = await RunTool(options.PgBackRest, ["help", "restore"], budget.Token);
            var pg = await RunTool(options.Postgres, ["--version"], budget.Token);
            var ctl = await RunTool(options.PgCtl, ["--version"], budget.Token);
            var psql = await RunTool(options.Psql, ["--version"], budget.Token);
            postgresMajor = Major(pg.Output);
            var match = br.Success && pg.Success && ctl.Success && psql.Success && postgresMajor >= 14
                && Major(ctl.Output) == postgresMajor && Major(psql.Output) == postgresMajor;
            Add(4, match ? CheckState.Passed : CheckState.Failed,
                match ? $"pgBackRest restore and PostgreSQL {postgresMajor} tools are available."
                    : "pgBackRest restore or matching PostgreSQL tools are unavailable.",
                "Use the BackupService image with pgBackRest and matching postgres, pg_ctl and psql tools.");
        }
        catch (Exception ex) when (IsCheckError(ex, ct))
        {
            LogCheckError(4, ex);
            Add(4, ex is OperationCanceledException ? CheckState.Unknown : CheckState.Failed,
                "The recovery tools could not be checked.", "Check the BackupService image and its PostgreSQL tools.");
        }

        try
        {
            repository = await client.GetRepositoryAsync(budget.Token);
            Add(5, repository.StatusCode is 0 or 2 ? CheckState.Passed : CheckState.Failed,
                repository.StatusMessage, "Check the backup volume, stanza and pgBackRest configuration.");
        }
        catch (Exception ex) when (IsCheckError(ex, ct))
        {
            LogCheckError(5, ex);
            Add(5, CheckState.Unknown, "pgBackRest could not read usable repository metadata.",
                "Check the repository mount and service logs for the pgBackRest error.");
        }

        var count = repository?.Backups.Count(backup => !backup.HasError);
        Add(6, count switch { null => CheckState.Unknown, 0 => CheckState.Failed, _ => CheckState.Passed },
            count is null ? "Backup availability could not be established." : $"{count} backup(s) without repository-reported errors.",
            "Mount a repository with a usable FrostStream backup.");
        return new(checks, repository, postgresMajor, free, populated);

        void Check(int index, string remediation, Func<(CheckState State, string Detail)> run)
        {
            try
            {
                var (state, detail) = run();
                Add(index, state, detail, remediation);
            }
            catch (Exception ex) when (IsCheckError(ex, ct))
            {
                LogCheckError(index, ex);
                Add(index, CheckState.Failed, ex.Message, remediation);
            }
        }

        void Add(int index, CheckState state, string detail, string remediation) =>
            checks.Add(new(CheckNames[index], state, detail, remediation, DateTimeOffset.UtcNow));
        void LogCheckError(int index, Exception ex) => logger.LogWarning(ex, "Recovery readiness check {Check} failed.", CheckNames[index]);

        (string? Directory, string Problem) CheckSocketMount()
        {
            try
            {
                // The kernel reports the mount under /run/postgresql when /var/run is a symlink.
                var resolved = ResolveSocketDirectory(new DirectoryInfo(Paths.Absolute(options.SocketPath)));
                // A container's own /run mount is not evidence of a shared socket volume.
                if (resolved.Exists && FindMount(resolved.FullName) is not (null or "/run" or "/var/run"))
                    return (resolved.FullName, "");

                return (null, $"The shared PostgreSQL socket volume is unavailable at {resolved.FullName}.");
            }
            catch (Exception ex) when (IsCheckError(ex, ct))
            {
                LogCheckError(2, ex);
                return (null, $"Could not resolve the configured socket directory {options.SocketPath}.");
            }
        }
    }

    private static DirectoryInfo ResolveSocketDirectory(DirectoryInfo directory, int remainingLinks = 40)
    {
        if (directory.Parent is not { } parent) return directory;
        var resolvedParent = ResolveSocketDirectory(parent, remainingLinks);
        var entry = new DirectoryInfo(Path.Combine(resolvedParent.FullName, directory.Name));
        if (entry.LinkTarget is null) return entry;
        if (remainingLinks == 0) throw new IOException("Too many symbolic links in the PostgreSQL socket path.");

        var target = entry.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"Could not resolve symbolic link {entry.FullName}.");
        return ResolveSocketDirectory(new DirectoryInfo(target.FullName), remainingLinks - 1);
    }

    public static string? FindMount(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var full = Path.GetFullPath(path);
        try
        {
            return File.ReadLines("/proc/self/mountinfo").Select(line => line.Split(' ')[4])
                .Select(value => Regex.Replace(value, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()))
                .Where(mount => mount != "/" && (full == mount || full.StartsWith(mount + "/", StringComparison.Ordinal)))
                .MaxBy(mount => mount.Length);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static int? Major(string version)
    {
        var match = Regex.Match(version, @"\b\d+");
        return match.Success && int.TryParse(match.Value, out var major) ? major : null;
    }

    private Task<CommandResult> RunTool(string tool, string[] args, CancellationToken ct) =>
        runner.RunAsync(new(tool, args, TimeSpan.FromSeconds(5)), cancellationToken: ct);

    private static bool IsCheckError(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested &&
        ex is IOException or UnauthorizedAccessException or Win32Exception or CommandFailedException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OperationCanceledException;
}
