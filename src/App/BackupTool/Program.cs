using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupTool;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<int> Main(string[] args)
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
        var name = options.Get("name") ?? $"froststream-core-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var backupDirectory = Path.GetFullPath(Path.Combine(outputRoot, SanitizeName(name)));
        if (Directory.Exists(backupDirectory))
            throw new InvalidOperationException($"Backup directory already exists: {backupDirectory}");

        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(Path.Combine(backupDirectory, "postgres"));
        Directory.CreateDirectory(Path.Combine(backupDirectory, "openbao"));
        Directory.CreateDirectory(Path.Combine(backupDirectory, "config"));

        var pg = PostgresOptions.From(options);
        foreach (var database in pg.Databases)
        {
            await RunPostgresToolAsync(
                pg,
                "pg_dump",
                [
                    "-h", pg.Host,
                    "-p", pg.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-U", pg.User,
                    "-F", "c",
                    "-d", database,
                    "-f", Path.Combine(backupDirectory, "postgres", $"{database}.dump")
                ]);
        }

        var bao = OpenBaoOptions.From(options);
        var openBaoExport = await ExportOpenBaoAsync(bao);
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "openbao", "kv-export.json"),
            JsonSerializer.Serialize(openBaoExport, JsonOptions));

        var requiredConfig = new BackupRequiredConfig
        {
            PostgresDatabases = pg.Databases,
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
            JsonSerializer.Serialize(requiredConfig, JsonOptions));

        var manifest = new BackupManifest
        {
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ToolVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            MediaIncluded = false,
            Components =
            [
                new BackupComponent("postgres", "logical-pg-dump", pg.Databases),
                new BackupComponent("openbao", "kv-v2-json", [bao.KvMount]),
                new BackupComponent("config", "restore-requirements", ["restore-requirements.json"])
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        var checksums = await ComputeChecksumsAsync(backupDirectory);
        await File.WriteAllLinesAsync(
            Path.Combine(backupDirectory, "checksums.sha256"),
            checksums.Select(x => $"{x.Hash}  {x.RelativePath}"));

        Console.WriteLine(backupDirectory);
        return 0;
    }

    private static async Task<int> VerifyBackupAsync(CliOptions options)
    {
        var archive = options.Required("archive");
        await ReadManifestAsync(archive);
        var expected = await ReadChecksumsAsync(archive);
        var actual = await ComputeChecksumsAsync(archive, includeChecksumFile: false);

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

        var manifest = await ReadManifestAsync(archive);
        if (manifest.MediaIncluded)
            throw new InvalidOperationException("Refusing to restore an archive that claims to include media.");

        var pg = PostgresOptions.From(options);
        foreach (var database in pg.Databases)
        {
            var dump = Path.Combine(archive, "postgres", $"{database}.dump");
            if (!File.Exists(dump))
                throw new FileNotFoundException($"Missing PostgreSQL dump for database '{database}'.", dump);

            await RunPostgresToolAsync(
                pg,
                "dropdb",
                [
                    "--if-exists",
                    "-h", pg.Host,
                    "-p", pg.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-U", pg.User,
                    database
                ],
                allowFailure: false);
            await RunPostgresToolAsync(
                pg,
                "createdb",
                [
                    "-h", pg.Host,
                    "-p", pg.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-U", pg.User,
                    database
                ]);
            await RunPostgresToolAsync(
                pg,
                "pg_restore",
                [
                    "-h", pg.Host,
                    "-p", pg.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-U", pg.User,
                    "-d", database,
                    "--clean",
                    "--if-exists",
                    dump
                ]);
        }

        var bao = OpenBaoOptions.From(options);
        var exportPath = Path.Combine(archive, "openbao", "kv-export.json");
        var export = JsonSerializer.Deserialize<OpenBaoKvExport>(
            await File.ReadAllTextAsync(exportPath),
            JsonOptions) ?? throw new InvalidOperationException("OpenBao export is invalid.");
        await RestoreOpenBaoAsync(bao, export);

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

    private static async Task<OpenBaoKvExport> ExportOpenBaoAsync(OpenBaoOptions options)
    {
        using var client = NewOpenBaoClient(options);
        var secrets = new List<OpenBaoSecret>();
        await ExportOpenBaoPathAsync(client, options.KvMount, "", secrets);
        return new OpenBaoKvExport
        {
            KvMount = options.KvMount,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Secrets = secrets.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray()
        };
    }

    private static async Task ExportOpenBaoPathAsync(
        HttpClient client,
        string mount,
        string path,
        List<OpenBaoSecret> secrets)
    {
        using var listRequest = new HttpRequestMessage(HttpMethod.Parse("LIST"), $"/v1/{mount}/metadata/{path}");
        using var listResponse = await client.SendAsync(listRequest);
        if (listResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        listResponse.EnsureSuccessStatusCode();

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var keys = listDoc.RootElement.GetProperty("data").GetProperty("keys").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();

        foreach (var key in keys)
        {
            if (key.EndsWith("/", StringComparison.Ordinal))
            {
                await ExportOpenBaoPathAsync(client, mount, path + key, secrets);
                continue;
            }

            var secretPath = path + key;
            using var readResponse = await client.GetAsync($"/v1/{mount}/data/{secretPath}");
            readResponse.EnsureSuccessStatusCode();
            using var readDoc = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in readDoc.RootElement.GetProperty("data").GetProperty("data").EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }

            secrets.Add(new OpenBaoSecret(secretPath, values));
        }
    }

    private static async Task RestoreOpenBaoAsync(OpenBaoOptions options, OpenBaoKvExport export)
    {
        if (!string.Equals(export.KvMount, options.KvMount, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"OpenBao export mount '{export.KvMount}' does not match target mount '{options.KvMount}'.");
        }

        using var client = NewOpenBaoClient(options);
        foreach (var secret in export.Secrets)
        {
            using var response = await client.PostAsync(
                $"/v1/{options.KvMount}/data/{secret.Path}",
                new StringContent(JsonSerializer.Serialize(new { data = secret.Values }), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
        }
    }

    private static HttpClient NewOpenBaoClient(OpenBaoOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
            throw new InvalidOperationException("OpenBao token is required for backup/restore.");

        var client = new HttpClient { BaseAddress = new Uri(options.Address, UriKind.Absolute) };
        client.DefaultRequestHeaders.Add("X-Vault-Token", options.Token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task RunPostgresToolAsync(
        PostgresOptions options,
        string toolName,
        IReadOnlyList<string> arguments,
        bool allowFailure = false)
    {
        var toolPath = string.IsNullOrWhiteSpace(options.BinDir)
            ? toolName
            : Path.Combine(options.BinDir, toolName);

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(options.Password))
            startInfo.Environment["PGPASSWORD"] = options.Password;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {toolName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !allowFailure)
        {
            var stderr = await stderrTask;
            var stdout = await stdoutTask;
            throw new InvalidOperationException($"{toolName} failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(string archive)
    {
        var manifestPath = Path.Combine(archive, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Backup manifest was not found.", manifestPath);

        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(manifestPath),
            JsonOptions) ?? throw new InvalidOperationException("Backup manifest is invalid.");

        if (manifest.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported backup schema version {manifest.SchemaVersion}.");
        if (manifest.MediaIncluded)
            throw new InvalidOperationException("Invalid core backup: mediaIncluded must be false.");

        return manifest;
    }

    private static async Task<IReadOnlyList<ChecksumEntry>> ComputeChecksumsAsync(
        string archive,
        bool includeChecksumFile = true)
    {
        var entries = new List<ChecksumEntry>();
        foreach (var file in Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(archive, file).Replace('\\', '/');
            if (!includeChecksumFile && string.Equals(relative, "checksums.sha256", StringComparison.Ordinal))
                continue;

            await using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            entries.Add(new ChecksumEntry(relative, hash));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<ChecksumEntry>> ReadChecksumsAsync(string archive)
    {
        var checksumPath = Path.Combine(archive, "checksums.sha256");
        if (!File.Exists(checksumPath))
            throw new FileNotFoundException("Backup checksum file was not found.", checksumPath);

        var entries = new List<ChecksumEntry>();
        foreach (var line in await File.ReadAllLinesAsync(checksumPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2)
                throw new InvalidOperationException($"Invalid checksum line: {line}");
            entries.Add(new ChecksumEntry(parts[1], parts[0]));
        }

        return entries;
    }

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
            FrostStream.BackupTool

            Commands:
              create --output <dir> [--name <name>]
              verify --archive <dir>
              restore --archive <dir> --force
              list --path <dir>

            Common options:
              --postgres-host <host>       default: env POSTGRES_HOST or localhost
              --postgres-port <port>       default: env POSTGRES_PORT or 5432
              --postgres-user <user>       default: env POSTGRES_USER or postgres
              --postgres-password <pass>   default: env POSTGRES_PASSWORD
              --postgres-bin-dir <dir>     optional directory containing pg_dump/pg_restore/dropdb/createdb
              --openbao-address <url>      default: env OPENBAO_ADDR/OpenBao__Address or http://127.0.0.1:8200
              --openbao-token <token>      default: env OPENBAO_TOKEN/OpenBao__Token
              --openbao-kv-mount <mount>   default: env OPENBAO_KV_MOUNT/OpenBao__KvMount or secret
            """);
    }
}

internal sealed record BackupManifest
{
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string ToolVersion { get; init; }
    public required bool MediaIncluded { get; init; }
    public required IReadOnlyList<BackupComponent> Components { get; init; }
}

internal sealed record BackupComponent(string Name, string Format, IReadOnlyList<string> Items);

internal sealed record BackupRequiredConfig
{
    public required IReadOnlyList<string> PostgresDatabases { get; init; }
    public required string OpenBaoAddress { get; init; }
    public required string OpenBaoKvMount { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal sealed record OpenBaoKvExport
{
    public required string KvMount { get; init; }
    public required DateTimeOffset ExportedAtUtc { get; init; }
    public required IReadOnlyList<OpenBaoSecret> Secrets { get; init; }
}

internal sealed record OpenBaoSecret(string Path, IReadOnlyDictionary<string, string> Values);

internal sealed record ChecksumEntry(string RelativePath, string Hash);

internal sealed record PostgresOptions(
    string Host,
    int Port,
    string User,
    string? Password,
    string? BinDir,
    IReadOnlyList<string> Databases)
{
    public static PostgresOptions From(CliOptions options)
        => new(
            options.Get("postgres-host") ?? Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost",
            int.TryParse(options.Get("postgres-port") ?? Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
            options.Get("postgres-user") ?? Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres",
            options.Get("postgres-password") ?? Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"),
            options.Get("postgres-bin-dir") ?? Environment.GetEnvironmentVariable("POSTGRES_BIN_DIR"),
            SplitCsv(options.Get("postgres-databases") ?? Environment.GetEnvironmentVariable("POSTGRES_DATABASES") ?? "froststreamdb,authentikdb,openfgadb"));

    private static IReadOnlyList<string> SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal sealed record OpenBaoOptions(string Address, string? Token, string KvMount)
{
    public static OpenBaoOptions From(CliOptions options)
        => new(
            options.Get("openbao-address")
            ?? Environment.GetEnvironmentVariable("OPENBAO_ADDR")
            ?? Environment.GetEnvironmentVariable("OpenBao__Address")
            ?? "http://127.0.0.1:8200",
            options.Get("openbao-token")
            ?? Environment.GetEnvironmentVariable("OPENBAO_TOKEN")
            ?? Environment.GetEnvironmentVariable("OpenBao__Token"),
            options.Get("openbao-kv-mount")
            ?? Environment.GetEnvironmentVariable("OPENBAO_KV_MOUNT")
            ?? Environment.GetEnvironmentVariable("OpenBao__KvMount")
            ?? "secret");
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public bool Has(string name) => _values.ContainsKey(name);

    public string Required(string name)
        => Get(name) ?? throw new InvalidOperationException($"Missing required option --{name}.");

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var result = new CliOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._values[name] = args[++i];
            }
            else
            {
                result._values[name] = "true";
            }
        }

        return result;
    }
}
