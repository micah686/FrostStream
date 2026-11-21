using System.Security.Cryptography;
using System.Text.Json;
using BackupService;
using BackupService.PgBackRest;
using BackupServiceNew.PgBackRestWrapper;
using Shared.Backups;

namespace BackupServiceNew;

internal sealed class LogicalBackups(BackupServiceOptions settings, ICommandRunner runner, OpenBaoPairing openBao)
{
    private string Root => Path.GetFullPath(settings.Directory);
    private sealed record Manifest(BackupInfoDto Backup, string[] Databases, Dictionary<string, string> Sha256)
    {
        public int FormatVersion { get; init; } = 1;
        public string[] Included => Sha256.Keys.Select(path => path.Split(Path.DirectorySeparatorChar)[0]).Distinct().Order().ToArray();
    }

    public IEnumerable<BackupInfoDto> List()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(Root, "backup-*"))
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) continue;
            Manifest? manifest;
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)); }
            catch (JsonException) { continue; }
            if (manifest is not null) yield return manifest.Backup;
        }
    }

    public async Task<BackupInfoDto> CreateAsync(string name, Guid id, bool includeOpenBao, bool includeOpenBaoKeys, bool includeDataProtectionKeys, IProgress<OutputLine> progress, CancellationToken ct)
    {
        if (includeOpenBaoKeys && (settings.OpenBaoKeysPath is null || !Directory.Exists(settings.OpenBaoKeysPath))) throw new InvalidOperationException("OpenBao key source is not configured or mounted.");
        if (includeDataProtectionKeys && (settings.DataProtectionKeysPath is null || !Directory.Exists(settings.DataProtectionKeysPath))) throw new InvalidOperationException("Data protection key source is not configured or mounted.");
        var started = DateTimeOffset.UtcNow;
        var safeName = string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var label = $"backup-{safeName}-{started:yyyyMMddTHHmmssfffZ}-{id:N}";
        var directory = Path.Combine(Root, label);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var data = Path.Combine(directory, "postgres_logical");
        Directory.CreateDirectory(data);
        foreach (var database in settings.ExpectedDatabases)
        {
            if (Path.GetFileName(database) != database || database is "." or "..") throw new InvalidOperationException("Invalid database name.");
            await Run("pg_dump", ["--host", settings.PostgresSocketDirectory, "--port", settings.PostgresPort.ToString(), "--username", settings.PostgresUser,
                "--no-password", "--format=directory", "--file", Path.Combine(data, database), "--dbname", database], progress, ct);
        }
        if (includeOpenBao)
        {
            await openBao.ExportForBackupAsync(label, ct);
            var target = Path.Combine(directory, "openbao");
            Directory.CreateDirectory(target);
            foreach (var extension in new[] { ".json", ".json.sha256", ".raft-snapshot", ".raft-snapshot.sha256" })
                File.Copy(Path.Combine(Root, "openbao", label + extension), Path.Combine(target, "snapshot" + extension));
        }
        if (includeOpenBaoKeys) CopyKeys(settings.OpenBaoKeysPath!, Path.Combine(directory, "openbao_keys"));
        if (includeDataProtectionKeys) CopyKeys(settings.DataProtectionKeysPath!, Path.Combine(directory, ".dataprotection"));
        var hashes = new Dictionary<string, string>();
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            hashes.Add(Path.GetRelativePath(directory, file), await Hash(file, ct));
            size += new FileInfo(file).Length;
        }
        var backup = new BackupInfoDto(label, "full", name, started, DateTimeOffset.UtcNow, size, size, null, null, false, includeOpenBao, includeDataProtectionKeys) { Mode = "logical" };
        var manifest = new Manifest(backup, settings.ExpectedDatabases, hashes);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.tmp"), JsonSerializer.Serialize(manifest), ct);
        File.Move(Path.Combine(directory, "manifest.tmp"), Path.Combine(directory, "manifest.json"));
        return backup;
    }

    public async Task VerifyAsync(string label, bool deep, IProgress<OutputLine> progress, CancellationToken ct)
    {
        if (Path.GetFileName(label) != label || !label.StartsWith("backup-", StringComparison.Ordinal)) throw new ArgumentException("Invalid backup label.");
        var directory = Path.Combine(Root, label);
        RejectLink(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        RejectLink(manifestPath);
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, ct)) ?? throw new InvalidDataException("Missing manifest.");
        if (manifest.Sha256.Count == 0) throw new InvalidDataException("Invalid logical manifest.");
        foreach (var entry in manifest.Sha256)
        {
            var file = Path.GetFullPath(Path.Combine(directory, entry.Key));
            if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Invalid manifest path.");
            for (var current = file; current != directory; current = Path.GetDirectoryName(current)!) RejectLink(current);
            if (!string.Equals(await Hash(file, ct), entry.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Checksum failed: {entry.Key}");
        }
        var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => file != manifestPath).Select(file => Path.GetRelativePath(directory, file)).Order().ToArray();
        if (!actualFiles.SequenceEqual(manifest.Sha256.Keys.Order())) throw new InvalidDataException("Backup file inventory differs from the manifest.");
        if (!deep) return;
        if (manifest.Backup.Mode != "logical") throw new InvalidOperationException("Physical verification requires pgBackRest.");
        var scratch = Path.Combine(Path.GetTempPath(), "logical-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cluster = Path.Combine(scratch, "data");
        var started = false;
        try
        {
            await Run("initdb", ["-D", cluster, "-U", settings.PostgresUser, "--auth-local=trust", "--auth-host=reject", "--no-locale"], progress, ct);
            await Run("pg_ctl", ["-D", cluster, "-l", Path.Combine(scratch, "postgres.log"), "-o", $"-c listen_addresses='' -c unix_socket_directories='{scratch}' -c unix_socket_permissions=0700", "-w", "start"], progress, ct);
            started = true;
            foreach (var database in manifest.Databases)
            {
                if (Path.GetFileName(database) != database || database is "." or "..") throw new InvalidDataException("Invalid database name.");
                var dump = Path.Combine(directory, "postgres_logical", database);
                if (!manifest.Sha256.ContainsKey(Path.GetRelativePath(directory, Path.Combine(dump, "toc.dat")))) throw new InvalidDataException("Missing dump inventory.");
                await Run("createdb", ["-h", scratch, "-U", settings.PostgresUser, "--", database], progress, ct);
                await Run("pg_restore", ["-h", scratch, "-U", settings.PostgresUser, "--exit-on-error", "--no-owner", "--no-privileges", "-d", database, dump], progress, ct);
                await Run("psql", ["-h", scratch, "-U", settings.PostgresUser, "-d", database, "-v", "ON_ERROR_STOP=1", "-c", "SELECT count(*) FROM pg_catalog.pg_class"], progress, ct);
            }
        }
        finally
        {
            if (started || File.Exists(Path.Combine(cluster, "postmaster.pid")))
                await Run("pg_ctl", ["-D", cluster, "-m", "immediate", "-w", "stop"], progress, CancellationToken.None);
            Directory.Delete(scratch, true);
        }
    }

    public string? FindPhysicalBundle(string label)
    {
        if (!Directory.Exists(Root)) return null;
        foreach (var directory in Directory.EnumerateDirectories(Root, "backup-*"))
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) continue;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path));
            if (manifest?.Backup is { Mode: "physical" } backup && backup.Label == label) return directory;
        }
        return null;
    }

    public async Task CreatePhysicalBundleAsync(BackupInfoDto backup, Guid id, string repositoryPath, bool includeOpenBao, bool includeOpenBaoKeys, bool includeDataProtectionKeys, CancellationToken ct)
    {
        var safeName = string.Concat((backup.Name ?? "physical").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var directory = Path.Combine(Root, $"backup-{safeName}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{id:N}");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Keep an independent repository copy, including dependencies and archived WAL.
        // Never hard-link repository files: retention and subsequent writes must not mutate a bundle.
        CopyRepository(repositoryPath, Path.Combine(directory, "postgres_physical", "pgbackrest"), ct);
        if (includeOpenBao)
        {
            var target = Path.Combine(directory, "openbao");
            Directory.CreateDirectory(target);
            foreach (var extension in new[] { ".json", ".json.sha256", ".raft-snapshot", ".raft-snapshot.sha256" })
                File.Copy(Path.Combine(Root, "openbao", backup.Label + extension), Path.Combine(target, "snapshot" + extension));
        }
        if (includeOpenBaoKeys) CopyKeys(settings.OpenBaoKeysPath ?? throw new InvalidOperationException("OpenBao key source is not configured."), Path.Combine(directory, "openbao_keys"));
        if (includeDataProtectionKeys) CopyKeys(settings.DataProtectionKeysPath ?? throw new InvalidOperationException("Data protection key source is not configured."), Path.Combine(directory, ".dataprotection"));
        var hashes = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            hashes.Add(Path.GetRelativePath(directory, file), await Hash(file, ct));
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.tmp"), JsonSerializer.Serialize(new Manifest(backup with { RepositorySize = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) }, settings.ExpectedDatabases, hashes)), ct);
        File.Move(Path.Combine(directory, "manifest.tmp"), Path.Combine(directory, "manifest.json"));
    }

    private static void CopyRepository(string source, string destination, CancellationToken ct)
    {
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            // pgBackRest's latest symlink is a convenience; backup.info and manifests identify sets.
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                if (Path.GetFileName(entry) == "latest") continue;
                throw new InvalidDataException("Unexpected symbolic link in repository.");
            }
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyRepository(entry, target, ct);
            else File.Copy(entry, target);
        }
    }

    private static void CopyKeys(string source, string destination)
    {
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLink(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyKeys(entry, target);
            else File.Copy(entry, target);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Backup paths cannot contain symbolic links.");
    }
    private static async Task<string> Hash(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    private async Task Run(string executable, string[] args, IProgress<OutputLine> progress, CancellationToken ct)
    {
        var result = await runner.RunAsync(new CommandSpec(executable, args, TimeSpan.FromHours(2)), progress, ct);
        if (!result.Success) throw new CommandFailedException(result);
    }
}
