using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DataBridge.Persistence.Sqlite;

/// <summary>Offline recovery into a new directory; never replaces a running installation.</summary>
public static class SqliteRecoveryCommand
{
    public static async Task<bool> RunAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        var snapshot = configuration["Recovery:Snapshot"];
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            if (configuration.GetSection("Recovery").Exists())
                throw new InvalidOperationException("Recovery:Snapshot is required for recovery commands.");
            return false;
        }
        snapshot = Path.GetFullPath(snapshot);
        var keys = snapshot + SqliteRecoveryArtifacts.KeysSuffix;
        var manifest = snapshot + SqliteRecoveryArtifacts.ManifestSuffix;
        await SqliteRecoveryArtifacts.VerifyAsync(snapshot, keys, manifest, ct);
        if (configuration.GetValue<bool>("Recovery:VerifyOnly")) return true;
        var destination = configuration["Recovery:Destination"];
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("Recovery:Destination must name a new directory, or use Recovery:VerifyOnly=true.");
        destination = Path.GetFullPath(destination);
        if (destination == keys || destination.StartsWith(keys + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Recovery destination must be outside the source key ring.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Recovery destination already exists. Preserve it and choose a new directory.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".recovery-" + Guid.NewGuid().ToString("N"));
        SqliteRecoveryArtifacts.CreatePrivateDirectory(staging);
        try
        {
            var database = Path.Combine(staging, "core.sqlite");
            File.Copy(snapshot, database);
            SqliteRecoveryArtifacts.MakePrivateFile(database);
            SqliteRecoveryArtifacts.CopyKeys(keys, database + ".keys", ct);
            File.Copy(manifest, database + SqliteRecoveryArtifacts.ManifestSuffix);
            // Verify staged bytes as well: a source changed during copying cannot be published.
            await SqliteRecoveryArtifacts.VerifyAsync(database, database + ".keys", database + SqliteRecoveryArtifacts.ManifestSuffix, ct);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync(ct);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS search_index_state(id INTEGER PRIMARY KEY CHECK(id=1),invalidated INTEGER NOT NULL); INSERT INTO search_index_state VALUES(1,1) ON CONFLICT(id) DO UPDATE SET invalidated=1;";
                await command.ExecuteNonQueryAsync(ct);
            }
            // This manifest describes the backup, not the now writable restored database.
            File.Delete(database + SqliteRecoveryArtifacts.ManifestSuffix);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, destination); // Publish database and complete key ring together.
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        return true;
    }
}
