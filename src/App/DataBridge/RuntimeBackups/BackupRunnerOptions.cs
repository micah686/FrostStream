using Shared.Backups;

namespace DataBridge.Backup;

public sealed class BackupRunnerOptions : IBackupToolSettings
{
    public const string SectionName = "Backup";

    public string Directory { get; init; } = Path.Combine(AppContext.BaseDirectory, "backups");

    public string ToolPath { get; init; } = "dotnet";

    public string ToolBaseArguments { get; init; } = "";

    public string PostgresHost { get; init; } = "localhost";

    public int PostgresPort { get; init; } = 5432;

    public string PostgresUser { get; init; } = "postgres";

    public string? PostgresPassword { get; init; }

    public string? PostgresBinDir { get; init; }

    public string? ArchiveDir { get; init; }

    public string OpenBaoAddress { get; init; } = "http://127.0.0.1:8200";

    public string? OpenBaoToken { get; init; }

    public string OpenBaoKvMount { get; init; } = "secret";
}
