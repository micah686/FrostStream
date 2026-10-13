using Microsoft.Extensions.Configuration;

namespace DataBridge.Persistence.Secrets;

/// <summary>The persistent key directory is a companion artifact to the SQLite database for backup/restore.</summary>
public sealed record LocalSecretStoreOptions(string KeyRingPath)
{
    public const string ApplicationName = "FrostStream.Lite.Secrets";

    public static LocalSecretStoreOptions FromConfiguration(IConfiguration configuration,
        PersistenceOptions persistence, string contentRoot)
    {
        var path = configuration["Secrets:Local:KeyRingPath"] ?? persistence.SqlitePath + ".keys";
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Secrets:Local:KeyRingPath must be a persistent directory path.");
        var resolved = Path.GetFullPath(path, contentRoot);
        if (resolved == persistence.SqlitePath || resolved == Path.GetPathRoot(resolved))
            throw new InvalidOperationException("Secrets:Local:KeyRingPath must be a dedicated key directory, separate from the database.");
        return new(resolved);
    }
}
