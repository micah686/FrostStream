using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BackupService;

internal sealed class BackupEngine(BackupServiceOptions settings, IConfiguration configuration)
{
    public async Task<string> CreateAsync(
        string outputRoot,
        string name,
        string modeValue,
        CancellationToken cancellationToken)
    {
        var mode = PostgresBackupFactory.ParseMode(modeValue);
        var backupDirectory = Path.GetFullPath(Path.Combine(outputRoot, SanitizeName(name)));
        if (Directory.Exists(backupDirectory))
            throw new InvalidOperationException($"Backup directory already exists: {backupDirectory}");

        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(Path.Combine(backupDirectory, "config"));

        var pg = PostgresOptions.From(settings, configuration);
        var runner = new PostgresToolRunner(pg);
        var strategy = PostgresBackupFactory.Create(mode, pg, runner);
        var postgresComponent = await strategy.BackupAsync(backupDirectory, cancellationToken);

        var bao = OpenBaoOptions.From(settings);
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
            JsonSerializer.Serialize(requiredConfig, BackupJson.Options),
            cancellationToken);

        var components = new List<BackupComponent> { postgresComponent };
        if (openBaoComponent is not null)
            components.Add(openBaoComponent);
        components.Add(new BackupComponent("config", "restore-requirements", ["restore-requirements.json"]));

        var manifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ToolVersion = typeof(BackupEngine).Assembly.GetName().Version?.ToString() ?? "unknown",
            MediaIncluded = false,
            Mode = mode,
            Components = components
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, BackupJson.Options),
            cancellationToken);

        var checksums = await ChecksumService.ComputeAsync(backupDirectory);
        await ChecksumService.WriteAsync(backupDirectory, checksums);
        return backupDirectory;
    }

    public async Task VerifyAsync(string archive, CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(archive, cancellationToken);

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

        var pg = PostgresOptions.From(settings, configuration);
        var validator = new PostgresBackupValidator(new PostgresToolRunner(pg));
        await validator.ValidateAsync(archive, manifest, cancellationToken);
    }

    internal static async Task<BackupManifest> ReadManifestAsync(string archive, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(archive, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Backup manifest was not found.", manifestPath);

        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            BackupJson.Options) ?? throw new InvalidOperationException("Backup manifest is invalid.");

        if (manifest.SchemaVersion is not (1 or 2))
            throw new InvalidOperationException($"Unsupported backup schema version {manifest.SchemaVersion}.");
        if (manifest.MediaIncluded)
            throw new InvalidOperationException("Invalid core backup: mediaIncluded must be false.");

        return manifest;
    }

    private static string SanitizeName(string name)
    {
        var chars = name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        var safe = new string(chars).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? $"froststream-core-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}" : safe;
    }
}
