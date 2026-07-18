namespace BackupService;

internal enum PostgresBackupMode
{
    Snapshot,
    Full,
    WalArchive
}

internal sealed record BackupManifest
{
    public const int CurrentSchemaVersion = 2;

    public required int SchemaVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string ToolVersion { get; init; }
    public required bool MediaIncluded { get; init; }

    /// <summary>
    /// PostgreSQL backup mode. Absent in schema v1 archives, which are always snapshots
    /// (the enum default), so old archives deserialize correctly.
    /// </summary>
    public PostgresBackupMode Mode { get; init; } = PostgresBackupMode.Snapshot;

    public required IReadOnlyList<BackupComponent> Components { get; init; }
}

internal sealed record BackupComponent(string Name, string Format, IReadOnlyList<string> Items);

internal sealed record BackupRequiredConfig
{
    public required IReadOnlyList<string> PostgresDatabases { get; init; }
    public required PostgresBackupMode PostgresMode { get; init; }
    public required string OpenBaoAddress { get; init; }
    public required string OpenBaoKvMount { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}
