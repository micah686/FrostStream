using System.Security.Cryptography;
using System.Text.Json;
using DataBridge.Persistence.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace DataBridge.Persistence.Sqlite;

/// <summary>Companion artifacts for offline recovery. Validation never changes the database or generates keys.</summary>
internal static class SqliteRecoveryArtifacts
{
    public const string KeysSuffix = ".keys";
    public const string ManifestSuffix = ".recovery.json";
    private sealed record Manifest(int Version, string DatabaseSha256, Dictionary<string, string> KeyFiles);

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void MakePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static Dictionary<string, string> Files(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Recovery key directories must not contain symbolic links or reparse points.");
                if (entry is DirectoryInfo child) Visit(child.FullName);
                else files.Add(Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/'), entry.FullName);
            }
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Recovery key directories must not be symbolic links or reparse points.");
        Visit(root);
        return files;
    }

    public static void CopyKeys(string source, string destination, CancellationToken cancellationToken)
    {
        CreatePrivateDirectory(destination);
        // A fresh installation may have no key ring yet and no persisted secrets. Decryption
        // validation below rejects a missing ring whenever the snapshot actually needs keys.
        if (!Directory.Exists(source)) return;
        foreach (var (relative, file) in Files(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, relative);
            CreatePrivateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            MakePrivateFile(target);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public static async Task WriteManifestAsync(string database, string keys, string manifestPath, CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, file) in Files(keys)) hashes.Add(relative, await HashAsync(file, cancellationToken));
        var manifest = new Manifest(1, await HashAsync(database, cancellationToken), hashes);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
        MakePrivateFile(manifestPath);
    }

    public static async Task VerifyAsync(string database, string keys, string manifestPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(keys) || !File.Exists(manifestPath))
            throw new InvalidDataException("Recovery verification failed: the companion key backup or recovery manifest is missing. Preserve the current database and complete key ring before restoring.");
        Manifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken))
                ?? throw new JsonException("Empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Recovery verification failed: the recovery manifest is invalid.", ex);
        }
        if (manifest.Version != 1 || manifest.KeyFiles is null ||
            !string.Equals(await HashAsync(database, cancellationToken), manifest.DatabaseSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery verification failed: database checksum or manifest version does not match.");
        var files = Files(keys);
        if (files.Count != manifest.KeyFiles.Count)
            throw new InvalidDataException("Recovery verification failed: the companion key ring is incomplete or has changed.");
        foreach (var (relative, expectedHash) in manifest.KeyFiles)
        {
            if (!files.TryGetValue(relative, out var file) ||
                !string.Equals(await HashAsync(file, cancellationToken), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("Recovery verification failed: a companion key file is missing or has changed.");
        }
        await ValidateSnapshotAsync(database, keys, cancellationToken);
    }

    public static async Task ValidateSnapshotAsync(string database, string keys, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recovery verification failed: SQLite integrity check did not pass.");
        command.CommandText = "PRAGMA foreign_key_check;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidDataException("Recovery verification failed: SQLite foreign key check did not pass.");

        var protection = DataProtectionProvider.Create(new DirectoryInfo(keys), builder =>
            builder.SetApplicationName(LocalSecretStoreOptions.ApplicationName).DisableAutomaticKeyGeneration());
        command.CommandText = "SELECT path, payload FROM local_secrets;";
        await using var secrets = await command.ExecuteReaderAsync(cancellationToken);
        while (await secrets.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? plaintext = null;
            try
            {
                plaintext = protection.CreateProtector("SqliteSecretStore", "v1", secrets.GetString(0))
                    .Unprotect((byte[])secrets.GetValue(1));
                if (JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) is null)
                    throw new JsonException("Empty secret document.");
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                throw new InvalidDataException("Recovery verification failed: encrypted secrets cannot be read with the companion key ring. Preserve the current database and key ring; no live files have been replaced.", ex);
            }
            finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
}
