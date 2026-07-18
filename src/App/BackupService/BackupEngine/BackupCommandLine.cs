using System.Text.Json;

namespace BackupService;

internal static class BackupCommandLine
{
    private static readonly HashSet<string> Commands = ["backup", "create", "verify", "restore", "list", "wal-archive", "-h", "--help"];

    public static bool ShouldHandle(string[] args)
        => args.Length > 0 && Commands.Contains(args[0]);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var command = args[0];
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return command switch
            {
                "wal-archive" => await PostgresWalArchive.HandleAsync(options),
                "backup" when options.Positional.FirstOrDefault() == "create" => await CreateBackupAsync(options),
                "backup" when options.Positional.FirstOrDefault() == "verify" => await VerifyBackupAsync(options),
                "backup" when options.Positional.FirstOrDefault() == "restore" => await RestoreBackupAsync(options),
                "backup" when options.Positional.FirstOrDefault() == "list" => ListBackups(options),
                "create" => await CreateBackupAsync(options),
                "verify" => await VerifyBackupAsync(options),
                "restore" => await RestoreBackupAsync(options),
                "list" => ListBackups(options),
                _ => Fail($"Unknown command '{string.Join(' ', args)}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> CreateBackupAsync(CliOptions options)
    {
        var outputRoot = options.Required("output");
        var mode = PostgresBackupFactory.ParseMode(options.Get("mode"));
        var name = options.Get("name") ?? $"froststream-{Slug(mode)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var backupDirectory = Path.GetFullPath(Path.Combine(outputRoot, SanitizeName(name)));
        if (Directory.Exists(backupDirectory))
            throw new InvalidOperationException($"Backup directory already exists: {backupDirectory}");

        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(Path.Combine(backupDirectory, "config"));

        var pg = PostgresOptions.From(options);
        var runner = new PostgresToolRunner(pg);
        var strategy = PostgresBackupFactory.Create(mode, pg, runner);
        var postgresComponent = await strategy.BackupAsync(backupDirectory, CancellationToken.None);

        var bao = OpenBaoOptions.From(options);
        BackupComponent? openBaoComponent = null;
        if (mode != PostgresBackupMode.WalArchive)
        {
            await new OpenBaoBackup().ExportToFileAsync(bao, Path.Combine(backupDirectory, "openbao", "kv-export.json"));
            openBaoComponent = new BackupComponent("openbao", "kv-v2-json", [bao.KvMount]);
        }

        var requiredConfig = new BackupRequiredConfig
        {
            PostgresDatabases = pg.Databases,
            PostgresMode = mode,
            OpenBaoAddress = bao.Address,
            OpenBaoKvMount = bao.KvMount,
            Notes =
            [
                "Media files are intentionally excluded.",
                "Typesense and NATS runtime state are intentionally excluded and should be rebuilt/restarted.",
                "Restore is cold/offline: stop FrostStream services before restoring."
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "config", "restore-requirements.json"),
            JsonSerializer.Serialize(requiredConfig, BackupJson.Options));

        var components = new List<BackupComponent> { postgresComponent };
        if (openBaoComponent is not null)
            components.Add(openBaoComponent);
        components.Add(new BackupComponent("config", "restore-requirements", ["restore-requirements.json"]));

        var manifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ToolVersion = typeof(BackupCommandLine).Assembly.GetName().Version?.ToString() ?? "unknown",
            MediaIncluded = false,
            Mode = mode,
            Components = components
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, BackupJson.Options));

        var checksums = await ChecksumService.ComputeAsync(backupDirectory);
        await ChecksumService.WriteAsync(backupDirectory, checksums);

        Console.WriteLine(backupDirectory);
        return 0;
    }

    private static async Task<int> VerifyBackupAsync(CliOptions options)
    {
        var archive = options.Required("archive");
        var manifest = await BackupEngine.ReadManifestAsync(archive);

        var expected = await ChecksumService.ReadAsync(archive);
        var actual = await ChecksumService.ComputeAsync(archive, includeChecksumFile: false);

        var actualMap = actual.ToDictionary(x => x.RelativePath, x => x.Hash, StringComparer.Ordinal);
        var expectedPaths = expected.Select(x => x.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var expectedEntry in expected)
        {
            if (!actualMap.TryGetValue(expectedEntry.RelativePath, out var actualHash))
                throw new InvalidOperationException($"Missing backup file: {expectedEntry.RelativePath}");
            if (!string.Equals(actualHash, expectedEntry.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Checksum mismatch: {expectedEntry.RelativePath}");
        }

        var unexpected = actualMap.Keys.Except(expectedPaths, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unexpected is not null)
            throw new InvalidOperationException($"Unexpected backup file: {unexpected}");

        var pg = PostgresOptions.From(options);
        var validator = new PostgresBackupValidator(new PostgresToolRunner(pg));
        await validator.ValidateAsync(archive, manifest, CancellationToken.None);

        Console.WriteLine("Backup verified.");
        return 0;
    }

    private static async Task<int> RestoreBackupAsync(CliOptions options)
    {
        var archive = options.Required("archive");
        var force = options.Has("force");
        await VerifyBackupAsync(options);

        if (!force)
        {
            throw new InvalidOperationException(
                "Restore is destructive. Re-run with --force after stopping FrostStream services and confirming the target is correct.");
        }

        var manifest = await BackupEngine.ReadManifestAsync(archive);
        if (manifest.MediaIncluded)
            throw new InvalidOperationException("Refusing to restore an archive that claims to include media.");

        var pg = PostgresOptions.From(options);
        var runner = new PostgresToolRunner(pg);
        var restore = new PostgresRestore(pg, runner);
        await restore.RestoreAsync(archive, manifest, RestoreTarget.From(options), CancellationToken.None);

        var exportPath = Path.Combine(archive, "openbao", "kv-export.json");
        if (File.Exists(exportPath))
        {
            var export = JsonSerializer.Deserialize<OpenBaoKvExport>(
                await File.ReadAllTextAsync(exportPath),
                BackupJson.Options) ?? throw new InvalidOperationException("OpenBao export is invalid.");
            await new OpenBaoBackup().RestoreAsync(OpenBaoOptions.From(options), export);
        }

        Console.WriteLine("Restore completed. Restart services and run a metadata search reindex.");
        return 0;
    }

    private static int ListBackups(CliOptions options)
    {
        var path = options.Required("path");
        foreach (var manifestPath in Directory.EnumerateFiles(path, "manifest.json", SearchOption.AllDirectories).Order())
        {
            var directory = Path.GetDirectoryName(manifestPath) ?? path;
            Console.WriteLine(directory);
        }

        return 0;
    }

    private static string Slug(PostgresBackupMode mode) => mode switch
    {
        PostgresBackupMode.Snapshot => "core",
        PostgresBackupMode.Full => "full",
        PostgresBackupMode.WalArchive => "wal-archive",
        _ => "core"
    };

    private static string SanitizeName(string name)
    {
        var chars = name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var safe = new string(chars).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? $"froststream-core-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}" : safe;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            FrostStream BackupService

            Commands:
              create --output <dir> [--name <name>] [--mode snapshot|full|wal-archive]
              verify --archive <dir>
              restore --archive <dir> --force [--pgdata <dir>] [--pg-ctl <path>]
                      [--target-time <iso> | --target-lsn <lsn> | --target-name <n> | --recover-latest]
                      [--archive-dir <dir>]
              list --path <dir>
              wal-archive setup   --archive-dir <dir> [--tool-command <cmd>]
              wal-archive receive %p %f --archive-dir <dir>   (called by server archive_command)
              wal-archive restore %f %p --archive-dir <dir>   (called by recovery restore_command)

            Backup modes:
              snapshot     Per-database pg_dump custom archive (default; quick logical snapshot).
              full         pg_basebackup physical cluster base backup (PITR base).
              wal-archive  Initialize continuous WAL archiving; emits server settings to apply.

            Common options:
              --postgres-host <host>       default: env POSTGRES_HOST or localhost
              --postgres-port <port>       default: env POSTGRES_PORT or 5432
              --postgres-user <user>       default: env POSTGRES_USER or postgres
              --postgres-password <pass>   default: env POSTGRES_PASSWORD
              --postgres-repl-user <user>  replication role for pg_basebackup; default: postgres-user
              --postgres-bin-dir <dir>     optional dir containing the PostgreSQL client tools
              --archive-dir <dir>          WAL archive store (wal-archive + PITR restore)
              --pgdata <dir>               target data directory for a full/PITR restore
              --pg-ctl <path>              optional pg_ctl path for stopping/starting the server
              --tool-command <cmd>         how PostgreSQL should invoke BackupService CLI commands
              --openbao-address <url>      default: env OPENBAO_ADDR/OpenBao__Address or http://127.0.0.1:25400
              --openbao-token <token>      default: env OPENBAO_TOKEN/OpenBao__Token
              --openbao-kv-mount <mount>   default: env OPENBAO_KV_MOUNT/OpenBao__KvMount or secret
            """);
    }
}
