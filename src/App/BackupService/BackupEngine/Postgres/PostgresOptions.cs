using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace BackupService;

internal sealed record PostgresOptions(
    string Host,
    int Port,
    string User,
    string? Password,
    string? BinDir,
    IReadOnlyList<string> Databases,
    string ReplicationUser,
    string? ArchiveDir,
    string? PgData,
    string? PgCtl,
    string? ToolCommand)
{
    public static PostgresOptions From(CliOptions options)
    {
        var user = options.Get("postgres-user")
                   ?? Environment.GetEnvironmentVariable("POSTGRES_USER")
                   ?? "postgres";

        return new PostgresOptions(
            options.Get("postgres-host") ?? Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost",
            int.TryParse(options.Get("postgres-port") ?? Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
            user,
            options.Get("postgres-password") ?? Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"),
            options.Get("postgres-bin-dir") ?? Environment.GetEnvironmentVariable("POSTGRES_BIN_DIR"),
            SplitCsv(options.Get("postgres-databases") ?? Environment.GetEnvironmentVariable("POSTGRES_DATABASES") ?? "froststreamdb,authentikdb,openfgadb"),
            options.Get("postgres-repl-user") ?? Environment.GetEnvironmentVariable("POSTGRES_REPL_USER") ?? user,
            options.Get("archive-dir") ?? Environment.GetEnvironmentVariable("POSTGRES_ARCHIVE_DIR"),
            options.Get("pgdata") ?? Environment.GetEnvironmentVariable("PGDATA"),
            options.Get("pg-ctl") ?? Environment.GetEnvironmentVariable("POSTGRES_PG_CTL"),
            options.Get("tool-command")
            ?? Environment.GetEnvironmentVariable("BACKUPSERVICE_COMMAND"));
    }

    public static PostgresOptions From(BackupServiceOptions settings, IConfiguration configuration)
    {
        var connectionParts = ParseConnectionString(configuration.GetConnectionString("froststreamdb"));
        var backupSection = configuration.GetSection(BackupServiceOptions.SectionName);
        var user = backupSection["PostgresUser"]
                   ?? GetPart(connectionParts, "Username", "User ID", "User")
                   ?? settings.PostgresUser;

        return new PostgresOptions(
            backupSection["PostgresHost"] ?? GetPart(connectionParts, "Host", "Server") ?? settings.PostgresHost,
            int.TryParse(backupSection["PostgresPort"] ?? GetPart(connectionParts, "Port"), out var port) ? port : settings.PostgresPort,
            user,
            backupSection["PostgresPassword"] ?? GetPart(connectionParts, "Password") ?? settings.PostgresPassword,
            settings.PostgresBinDir,
            SplitCsv(backupSection["PostgresDatabases"] ?? Environment.GetEnvironmentVariable("POSTGRES_DATABASES") ?? "froststreamdb,authentikdb,openfgadb"),
            backupSection["PostgresReplicationUser"] ?? Environment.GetEnvironmentVariable("POSTGRES_REPL_USER") ?? user,
            settings.ArchiveDir,
            null,
            null,
            settings.ToolCommand);
    }

    /// <summary>Resolves a PostgreSQL client tool, honoring an optional <see cref="BinDir"/>.</summary>
    public string ToolPath(string tool)
        => string.IsNullOrWhiteSpace(BinDir) ? tool : Path.Combine(BinDir, tool);

    /// <summary>Standard host/port/user connection arguments shared by the client tools.</summary>
    public IReadOnlyList<string> ConnectionArgs(string? user = null)
        => ["-h", Host, "-p", Port.ToString(CultureInfo.InvariantCulture), "-U", user ?? User];

    private static IReadOnlyList<string> SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string> ParseConnectionString(string? connectionString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connectionString))
            return result;

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2)
                result[parts[0].Trim()] = parts[1].Trim();
        }

        return result;
    }

    private static string? GetPart(IReadOnlyDictionary<string, string> parts, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parts.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
