using Microsoft.Extensions.Configuration;
using Shared.Deployment;

namespace DataBridge.Persistence;

public enum PersistenceProvider { Postgres, Sqlite }

/// <summary>Resolved once at startup. Lite owns SQLite; Full owns PostgreSQL.</summary>
public sealed record PersistenceOptions(PersistenceProvider Provider, string SqlitePath, int BusyTimeoutSeconds)
{
    public const string DefaultSqlitePath = "/data/core.sqlite";

    public static PersistenceOptions FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        var mode = DeploymentOptions.FromConfiguration(configuration).Mode;
        if (configuration["Persistence:Sqlite:Enabled"] is not null)
            throw new InvalidOperationException("Persistence:Sqlite:Enabled is obsolete. The edition determines its database provider.");
        if (mode == DeploymentMode.Full)
            return new(PersistenceProvider.Postgres, DefaultSqlitePath, 5);

        var path = configuration["Persistence:Sqlite:Path"] ?? DefaultSqlitePath;
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:" || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Persistence:Sqlite:Path must be a persistent database file path.");
        var seconds = configuration.GetValue("Persistence:Sqlite:BusyTimeoutSeconds", 5);
        if (seconds is < 1 or > 60)
            throw new InvalidOperationException("Persistence:Sqlite:BusyTimeoutSeconds must be between 1 and 60.");
        return new(PersistenceProvider.Sqlite, Path.GetFullPath(path, contentRoot), seconds);
    }
}
