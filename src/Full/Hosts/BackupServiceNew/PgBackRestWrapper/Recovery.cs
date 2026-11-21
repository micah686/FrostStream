using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupServiceNew.PgBackRestWrapper;

public sealed partial class PgBackRestClient
{
    /// <summary>Restores the selected backup according to the requested recovery mode and records a receipt.</summary>
    /// <param name="request">Restore target, confirmations, and recovery settings.</param><param name="progress">Optional command output receiver.</param>
    /// <param name="cancellationToken">Token used to cancel restoration.</param>
    public Task<RestoreReceipt> RestoreAsync(RestoreRequest request, IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default) => Exclusive(() => Restore(request, progress, cancellationToken), cancellationToken);

    /// <summary>Selects a compatible backup, validates the destination, and performs the restore.</summary>
    /// <param name="request">Restore target, confirmations, and recovery settings.</param><param name="progress">Optional command output receiver.</param><param name="ct">Cancellation token.</param>
    private async Task<RestoreReceipt> Restore(RestoreRequest request, IProgress<OutputLine>? progress, CancellationToken ct)
    {
        // PostgreSQL timestamps have microsecond precision; persist the exact target sent to the engine.
        if (request.Timestamp is { } timestamp)
            request = request with { Timestamp = new DateTimeOffset(timestamp.UtcTicks - timestamp.UtcTicks % 10, TimeSpan.Zero) };
        ValidateRestore(request);
        var compatibility = await Compatibility(ct);
        var repository = await GetRepositoryAsync(ct);
        if (repository.StatusCode != 0) throw new InvalidOperationException($"Repository is unavailable: {repository.StatusMessage}");
        IEnumerable<BackupInfo> candidates = repository.Backups.Where(b => !b.HasError);
        if (request.BackupLabel != null) candidates = candidates.Where(b => b.Label == request.BackupLabel);
        if (request.Mode == RecoveryMode.Timestamp) candidates = candidates.Where(b => b.Finished <= request.Timestamp);
        if (request.Mode == RecoveryMode.Lsn)
        {
            var target = ParseLsn(request.Target!);
            candidates = candidates.Where(b => b.Metadata.TryGetProperty("lsn", out var lsn) &&
                lsn.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.String && ParseLsn(stop.GetString()!) <= target);
        }
        var backup = candidates.OrderByDescending(b => b.Finished).FirstOrDefault()
            ?? throw new InvalidOperationException("No usable backup precedes the target or matches the selected label.");
        var databaseId = backup.Metadata.GetProperty("database").GetProperty("id").GetInt32();
        var db = repository.Metadata.GetProperty("db").EnumerateArray().Single(d => d.GetProperty("id").GetInt32() == databaseId);
        var backupVersion = db.GetProperty("version").GetString()!;
        if (Major(compatibility.PostgresVersion) != Major(backupVersion))
            throw new InvalidOperationException($"Backup requires PostgreSQL {backupVersion}; installed tools are {compatibility.PostgresVersion}.");
        // Recheck immediately before launching the destructive command.
        ValidateRestore(request);
        var args = new List<string> { $"--pg1-path={Paths.Absolute(request.Destination)}", "--reset-pg1-host", "--no-link-all",
            $"--tablespace-map-all={Path.Combine(Paths.Absolute(request.Destination), "wrapper-tablespaces")}",
            $"--set={backup.Label}",
            $"--type={request.Mode switch { RecoveryMode.Consistency => "immediate", RecoveryMode.Latest => "default", RecoveryMode.Timestamp => "time", RecoveryMode.RestorePoint => "name", _ => "lsn" }}" };
        if (request.Mode != RecoveryMode.Latest)
            args.Add($"--target-action={(request.Mode == RecoveryMode.Consistency ? "promote" : "pause")}");
        if (request.Delta) args.Add("--delta");
        if (request.Mode != RecoveryMode.Consistency) args.Add($"--target-timeline={request.Timeline}");
        if (request.Mode == RecoveryMode.Timestamp) args.Add($"--target={request.Timestamp!.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}+00");
        if (request.Mode is RecoveryMode.RestorePoint or RecoveryMode.Lsn) args.Add($"--target={request.Target}");
        if (request.Exclusive) args.Add("--target-exclusive");
        var command = await BackRest("restore", ct, progress, args.ToArray());
        var receipt = new RestoreReceipt(command.Id, request, backup.Label, RecoveryStage.FilesRestored, DateTimeOffset.UtcNow,
            "pgBackRest restored files. PostgreSQL has not been started or recovery validated. WAL coverage beyond backup consistency is unproven.", backupVersion);
        await SaveReceipt(receipt);
        return receipt;
    }
    /// <summary>Validates restore confirmations, targets, destination safety, and available space.</summary>
    /// <param name="request">Restore request to validate.</param>
    private void ValidateRestore(RestoreRequest request)
    {
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentException("Invalid recovery mode.");
        if (request.ConfirmStanza != options.Stanza || !request.ConfirmServerStopped)
            throw new InvalidOperationException("Exact stanza confirmation and an explicit stopped-server attestation are required.");
        if (request.BackupLabel != null) Label(request.BackupLabel);
        if (request.Mode == RecoveryMode.RestorePoint && request.BackupLabel == null)
            throw new ArgumentException("Named-point recovery requires an explicit backup preceding the restore point.");
        if (request.Mode == RecoveryMode.Timestamp && (request.Timestamp == null || request.Timestamp > DateTimeOffset.UtcNow))
            throw new ArgumentException("Timestamp recovery requires a non-future UTC instant.");
        if (request.Mode != RecoveryMode.Timestamp && request.Timestamp != null) throw new ArgumentException("Timestamp only applies to timestamp recovery.");
        if (request.Mode is RecoveryMode.RestorePoint or RecoveryMode.Lsn)
        {
            if (string.IsNullOrWhiteSpace(request.Target) || request.Target.Length > 128 || request.Target.Any(char.IsControl))
                throw new ArgumentException("A printable recovery target is required.");
            if (request.Mode == RecoveryMode.Lsn) ParseLsn(request.Target);
        }
        else if (request.Target != null) throw new ArgumentException("Target applies only to named-point or LSN recovery.");
        if (request.Exclusive && request.Mode is not (RecoveryMode.Timestamp or RecoveryMode.Lsn)) throw new ArgumentException("Exclusive applies only to timestamp or LSN recovery.");
        if (!Regex.IsMatch(request.Timeline, @"^(latest|current|[1-9][0-9]*)$")) throw new ArgumentException("Invalid timeline.");
        var destination = Paths.Absolute(request.Destination);
        Paths.Disjoint(destination, options.RepositoryPath); Paths.Disjoint(destination, options.StatePath);
        Paths.Disjoint(destination, options.ConfigPath);
        if (destination != Paths.Absolute(options.DataPath)) Paths.Disjoint(destination, options.DataPath);
        Paths.NoLinks(destination);
        if (File.Exists(Path.Combine(destination, "postmaster.pid")))
            throw new InvalidOperationException("postmaster.pid exists: stop PostgreSQL and resolve any stale PID explicitly before restoring.");
        // Reject existing links too: a delta restore must not write through to external storage.
        if (Directory.Exists(destination)) RejectTreeLinks(destination);
        Paths.Writable(destination);
        if (request.MinimumFreeBytes < 0) throw new ArgumentException("Minimum free bytes cannot be negative.");
        if (Paths.FreeBytes(destination) < Math.Max(options.MinimumFreeBytes, request.MinimumFreeBytes)) throw new IOException("Insufficient destination free capacity.");
        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            if (!request.Delta) throw new InvalidOperationException("A populated destination requires Delta. Delta can remove or replace existing data.");
        }
        if (request.PreservedRollbackDirectory != null)
        {
            var rollback = Paths.Absolute(request.PreservedRollbackDirectory);
            Paths.NoLinks(rollback); Paths.Disjoint(rollback, destination);
            Paths.Disjoint(rollback, options.RepositoryPath); Paths.Disjoint(rollback, options.StatePath);
            if (!File.Exists(Path.Combine(rollback, "PG_VERSION")) || !Directory.Exists(Path.Combine(rollback, "base")))
                throw new IOException("Rollback directory must contain a preserved PostgreSQL data directory (PG_VERSION and base).");
        }
    }
    /// <summary>Recursively rejects symbolic links below an existing restore destination.</summary>
    /// <param name="root">Directory tree to inspect.</param>
    private static void RejectTreeLinks(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget != null) throw new IOException($"Existing symlink refused during restore: {entry.FullName}");
            if ((entry.Attributes & FileAttributes.Directory) != 0) RejectTreeLinks(entry.FullName);
        }
    }
    /// <summary>Converts a PostgreSQL hexadecimal LSN into its 64 bit numeric representation.</summary>
    /// <param name="value">LSN in X/X hexadecimal notation.</param>
    private static ulong ParseLsn(string value)
    {
        if (!Regex.IsMatch(value, "^[0-9A-Fa-f]{1,8}/[0-9A-Fa-f]{1,8}$")) throw new ArgumentException("Invalid PostgreSQL LSN.");
        var parts = value.Split('/');
        return ((ulong)uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32) |
            uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
    /// <summary>Persists a restore receipt under its unique identifier.</summary>
    /// <param name="receipt">Receipt to save.</param>
    private Task SaveReceipt(RestoreReceipt receipt) => reports.WriteAsync($"restore-{receipt.Id:N}.json", receipt);
    /// <summary>Reads a previously saved restore receipt.</summary>
    /// <param name="id">Identifier of the restore operation.</param>
    public Task<RestoreReceipt> ReadRestoreAsync(Guid id) => reports.ReadAsync<RestoreReceipt>($"restore-{id:N}.json");

    /// <summary>Observes the restored instance. Targeted recovery stays paused for inspection; this method never promotes it.</summary>
    /// <summary>Observes recovery state and optional SQL assertions without promoting a paused instance.</summary>
    /// <param name="id">Identifier of the restore receipt to update.</param><param name="socketPath">PostgreSQL Unix socket directory.</param>
    /// <param name="port">PostgreSQL port.</param><param name="assertions">Optional SQL assertions to run after reaching the target.</param>
    /// <param name="cancellationToken">Token used to cancel observation.</param>
    public Task<RestoreReceipt> ObserveRecoveryAsync(Guid id, string socketPath, int port,
        IReadOnlyList<SqlAssertion>? assertions = null, CancellationToken cancellationToken = default) =>
        Exclusive(() => Observe(id, socketPath, port, assertions, cancellationToken), cancellationToken);
    /// <summary>Queries the restored server, updates its receipt, and runs assertions once recovery reaches its target.</summary>
    /// <param name="id">Restore receipt identifier.</param><param name="socketPath">PostgreSQL Unix socket directory.</param>
    /// <param name="port">PostgreSQL port.</param><param name="assertions">Optional SQL assertions.</param><param name="ct">Cancellation token.</param>
    private async Task<RestoreReceipt> Observe(Guid id, string socketPath, int port, IReadOnlyList<SqlAssertion>? assertions, CancellationToken ct)
    {
        var receipt = await ReadRestoreAsync(id);
        receipt = receipt with { Stage = RecoveryStage.RecoveryPending, UpdatedAt = DateTimeOffset.UtcNow, Evidence = "Waiting for SQL recovery evidence." };
        await SaveReceipt(receipt);
        var observation = await Sql("SELECT json_build_object('directory', current_setting('data_directory'), 'recovering', pg_is_in_recovery(), 'paused', CASE WHEN pg_is_in_recovery() THEN pg_get_wal_replay_pause_state() = 'paused' ELSE false END, 'lsn', pg_last_wal_replay_lsn(), 'target_time', current_setting('recovery_target_time'), 'target_name', current_setting('recovery_target_name'), 'target_lsn', current_setting('recovery_target_lsn'));",
            "postgres", socketPath, port, ct);
        using var json = JsonDocument.Parse(observation.Output.Trim()); var state = json.RootElement;
        if (Paths.Absolute(state.GetProperty("directory").GetString()!) != Paths.Absolute(receipt.Request.Destination))
            throw new InvalidOperationException("SQL endpoint is not the restored destination.");
        var targeted = receipt.Request.Mode is RecoveryMode.Timestamp or RecoveryMode.RestorePoint or RecoveryMode.Lsn;
        var reached = targeted ? state.GetProperty("paused").GetBoolean() : !state.GetProperty("recovering").GetBoolean();
        // A paused server only counts when it carries the requested target configuration.
        if (targeted && reached)
        {
            reached = receipt.Request.Mode switch
            {
                RecoveryMode.Timestamp => DateTimeOffset.TryParse(state.GetProperty("target_time").GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var time) && time == receipt.Request.Timestamp,
                RecoveryMode.RestorePoint => state.GetProperty("target_name").GetString() == receipt.Request.Target,
                _ => state.GetProperty("target_lsn").GetString() is { Length: > 0 } observedLsn &&
                     ParseLsn(observedLsn) == ParseLsn(receipt.Request.Target!)
            };
        }
        var stage = reached ? RecoveryStage.TargetReached : RecoveryStage.RecoveryPending;
        receipt = receipt with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow, Evidence = observation.Output.Trim() };
        await SaveReceipt(receipt);
        if (reached && assertions is { Count: > 0 })
        {
            foreach (var assertion in assertions)
            {
                var result = await Sql(assertion.Sql, assertion.Database, socketPath, port, ct);
                if (result.Output.Trim() != assertion.ExpectedOutput.Trim()) throw new InvalidOperationException($"Recovery assertion failed in {assertion.Database}.");
            }
            stage = RecoveryStage.Validated;
        }
        receipt = receipt with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow, Evidence = observation.Output.Trim() +
            (receipt.Request.Mode == RecoveryMode.Latest ? " End of available archive replay is not proof that every source WAL segment was archived." : "") };
        await SaveReceipt(receipt); return receipt;
    }

    /// <summary>Restores into a fresh private directory, starts PostgreSQL without TCP or archiving, validates SQL, stops it, then removes only that directory.</summary>
    /// <summary>Restores into an isolated scratch directory, starts PostgreSQL, validates recovery, then cleans up.</summary>
    /// <param name="request">Scratch root, optional backup label, databases, and SQL assertions.</param><param name="progress">Optional command output receiver.</param>
    /// <param name="cancellationToken">Token used to cancel the scratch validation.</param>
    public Task<ScratchResult> TestRestoreAsync(ScratchRequest request, IProgress<OutputLine>? progress = null,
        CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        if (request.Mode is not (RecoveryMode.Consistency or RecoveryMode.Latest or RecoveryMode.Timestamp))
            throw new ArgumentException("Unsupported rehearsal recovery mode.");
        var started = DateTimeOffset.UtcNow;
        var root = Paths.Absolute(request.RootDirectory);
        foreach (var path in new[] { options.DataPath, options.RepositoryPath, options.StatePath, options.ConfigPath }) Paths.Disjoint(root, path);
        Paths.Writable(root);
        var directory = Path.Combine(root, $"pgbackrest-test-{Guid.NewGuid():N}");
        var data = Path.Combine(directory, "data"); var socket = Path.Combine(directory, "socket");
        Directory.CreateDirectory(data); Directory.CreateDirectory(socket);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var attemptedStart = false; var safeToDelete = true;
        try
        {
            var receipt = await Restore(new RestoreRequest { Destination = data, ConfirmStanza = options.Stanza, ConfirmServerStopped = true,
                BackupLabel = request.BackupLabel, Mode = request.Mode, Timestamp = request.Timestamp, MinimumFreeBytes = request.MinimumFreeBytes }, progress, cancellationToken);
            var config = Path.Combine(directory, "postgresql.conf");
            static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            await File.WriteAllTextAsync(config, $"data_directory={Quote(data)}\nlisten_addresses=''\nunix_socket_directories={Quote(socket)}\nport=5432\narchive_mode=off\narchive_command=''\nssl=off\nshared_preload_libraries=''\nhot_standby=on\nhba_file={Quote(Path.Combine(directory, "pg_hba.conf"))}\nident_file={Quote(Path.Combine(directory, "pg_ident.conf"))}\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "pg_hba.conf"), "local all all trust\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "pg_ident.conf"), "", cancellationToken);
            // auto.conf from the source may contain unsafe paths or replication connections. Preserve only restore-generated recovery settings.
            var auto = Path.Combine(data, "postgresql.auto.conf");
            if (File.Exists(auto))
            {
                var lines = await File.ReadAllLinesAsync(auto, cancellationToken);
                var keep = lines.Where(l => Regex.IsMatch(l, @"^\s*(restore_command|recovery_target(?:_\w+)?)\s*="));
                await File.WriteAllLinesAsync(auto, keep, cancellationToken);
            }
            // pg_ctl parses -o itself; reject quotes/backslashes rather than handing it ambiguous shell syntax.
            if (config.Any(c => c is '\'' or '"' or '\\' || char.IsWhiteSpace(c))) throw new ArgumentException("Scratch root must not contain whitespace, quotes or backslashes.");
            attemptedStart = true; safeToDelete = false;
            await Run(options.PgCtl, ["-D", data, "-l", Path.Combine(directory, "postgres.log"), "-w", "-t",
                Math.Max(1, (int)options.RecoveryTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture), "-o", $"-c config_file={config}", "start"], cancellationToken, progress, timeout: options.RecoveryTimeout + TimeSpan.FromSeconds(10));
            var checks = new List<string>();
            var assertions = request.Assertions is { Count: > 0 } ? request.Assertions : [new SqlAssertion("postgres", "SELECT 1;", "1")];
            using (var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                recoveryDeadline.CancelAfter(options.RecoveryTimeout);
                do
                {
                    receipt = await Observe(receipt.Id, socket, 5432, assertions, recoveryDeadline.Token);
                    if (receipt.Stage == RecoveryStage.Validated) break;
                    await Task.Delay(250, recoveryDeadline.Token);
                } while (true);
            }
            if (receipt.Stage != RecoveryStage.Validated) throw new InvalidOperationException("Scratch PostgreSQL recovery has not completed.");
            foreach (var db in request.ExpectedDatabases ?? [])
            {
                var result = await Sql("SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%';", db, socket, 5432, cancellationToken);
                if (!long.TryParse(result.Output.Trim(), out var count) || count < 1) throw new InvalidOperationException($"No user tables found in expected database {db}.");
                checks.Add($"{db}: {count} user tables");
            }
            checks.AddRange(assertions.Select(a => $"SQL assertion passed in {a.Database}"));
            await Run(options.PgCtl, ["-D", data, "-m", "immediate", "-w", "stop"], CancellationToken.None, timeout: TimeSpan.FromSeconds(60));
            safeToDelete = true;
            var resultReport = new ScratchResult(directory, receipt, checks, DateTimeOffset.UtcNow - started, true);
            await reports.WriteAsync($"scratch-{receipt.Id:N}.json", resultReport);
            return resultReport;
        }
        catch (Exception ex)
        {
            await reports.WriteAsync($"scratch-failure-{Guid.NewGuid():N}.json", new { Directory = directory, At = DateTimeOffset.UtcNow, Error = ex.ToString(),
                PostgresLog = File.Exists(Path.Combine(directory, "postgres.log")) ? await File.ReadAllTextAsync(Path.Combine(directory, "postgres.log")) : null });
            throw;
        }
        finally
        {
            if (attemptedStart && !safeToDelete)
            {
                var stop = await Run(options.PgCtl, ["-D", data, "-m", "immediate", "-w", "stop"], CancellationToken.None,
                    requireSuccess: false, timeout: TimeSpan.FromSeconds(60));
                safeToDelete = stop.Success;
            }
            // Preserve files if shutdown cannot be proven. Never recursively follow restored tablespace symlinks.
            if (safeToDelete && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }, cancellationToken);
}
