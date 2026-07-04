namespace Shared.Backups;

/// <summary>
/// Configuration shared by every host that shells out to the BackupTool CLI (WebAPI's
/// on-demand admin surface and DataBridge's scheduled runner). Bound from the <c>Backup</c>
/// configuration section.
/// </summary>
public interface IBackupToolSettings
{
    /// <summary>Directory backups are written to and scanned from.</summary>
    string Directory { get; }

    /// <summary>Executable used to launch the tool (e.g. <c>dotnet</c>).</summary>
    string ToolPath { get; }

    /// <summary>Leading arguments before the command (e.g. <c>run --project … --</c>).</summary>
    string ToolBaseArguments { get; }

    string PostgresHost { get; }

    int PostgresPort { get; }

    string PostgresUser { get; }

    string? PostgresPassword { get; }

    /// <summary>Optional directory containing the PostgreSQL client tools.</summary>
    string? PostgresBinDir { get; }

    /// <summary>WAL archive store for full/wal-archive backups and point-in-time restores.</summary>
    string? ArchiveDir { get; }

    string OpenBaoAddress { get; }

    string? OpenBaoToken { get; }

    string OpenBaoKvMount { get; }
}
