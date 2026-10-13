using System.Security.Cryptography;
using System.Text.Json;
using DataBridge.Persistence.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Shared.Secrets;

namespace DataBridge.Persistence.Secrets;

/// <summary>Authenticated encrypted secret documents. Keys are persisted separately from the database.</summary>
public sealed class SqliteSecretStore : ISecretStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly IDataProtectionProvider protection;

    public SqliteSecretStore(SqliteConnectionFactory factory, LocalSecretStoreOptions options)
    {
        this.factory = factory;
        // Use a dedicated provider so WebAPI's cookie/token key configuration cannot change the
        // application discriminator or storage location of persisted local secrets.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(options.KeyRingPath);
        else
        {
            Directory.CreateDirectory(options.KeyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var sharedAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(options.KeyRingPath) & sharedAccess) != 0)
                throw new InvalidOperationException("The local secret key directory must be accessible only to its owner (chmod 700).");
        }
        protection = DataProtectionProvider.Create(new DirectoryInfo(options.KeyRingPath),
            builder => builder.SetApplicationName(LocalSecretStoreOptions.ApplicationName));
    }

    private IDataProtector Protector(string path) => protection.CreateProtector("SqliteSecretStore", "v1", path);

    public async Task<IReadOnlyDictionary<string, string>?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM local_secrets WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        if (await command.ExecuteScalarAsync(cancellationToken) is not byte[] payload) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = Protector(path).Unprotect(payload);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                ?? throw new CryptographicException("The local secret document is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task WriteAsync(string path, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(values);
        byte[] payload;
        try { payload = Protector(path).Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO local_secrets(path,payload) VALUES($path,$payload) ON CONFLICT(path) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_secrets WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
