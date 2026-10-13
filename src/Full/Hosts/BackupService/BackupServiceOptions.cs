namespace BackupService;

public sealed class BackupServiceOptions
{
    public const string SectionName = "Backup";

    /// <summary>Backup root (the shared bind mount): job records, OpenBao exports, deep-verify scratch.</summary>
    public string Directory { get; init; } = Path.Combine(AppContext.BaseDirectory, "backups");

    /// <summary>pgBackRest stanza name; must match pgbackrest.conf and the server's archive_command.</summary>
    public string Stanza { get; init; } = "froststream";

    /// <summary>The live cluster's PGDATA inside the shared data volume; pgBackRest restores here.</summary>
    public string PgDataPath { get; init; } = "/var/lib/postgresql/18/docker";

    /// <summary>Shared unix-socket directory of the live postgres server.</summary>
    public string PostgresSocketDirectory { get; init; } = "/var/run/postgresql";

    public string PostgresHost { get; init; } = "localhost";
    public int PostgresPort { get; init; } = 5432;
    public string PostgresUser { get; init; } = "postgres";
    public string? PostgresPassword { get; init; }

    /// <summary>Databases a deep verify requires to exist in the restored cluster.</summary>
    public string[] ExpectedDatabases { get; init; } = ["froststreamdb", "authentikdb", "openfgadb"];

    public string OpenBaoAddress { get; init; } = "http://127.0.0.1:25400";
    public string? OpenBaoToken { get; init; }
    public string OpenBaoKvMount { get; init; } = "secret";

    /// <summary>Static token guarding the restore wizard; auth is disabled while unset.</summary>
    public string? RestoreUiToken { get; init; }

    /// <summary>Kestrel port serving the restore wizard; /internal/* is rejected on it.</summary>
    public int RestoreUiPort { get; init; } = 8081;
}
