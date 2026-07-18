namespace BackupService;

/// <summary>Point-in-time recovery target for a full-backup restore.</summary>
internal sealed record RestoreTarget(string? Time, string? Lsn, string? Name, bool RecoverLatest, string? ArchiveDir)
{
    public bool HasTarget => Time is not null || Lsn is not null || Name is not null || RecoverLatest;

    public static RestoreTarget From(CliOptions options)
        => new(
            options.Get("target-time"),
            options.Get("target-lsn"),
            options.Get("target-name"),
            options.Has("recover-latest"),
            options.Get("archive-dir"));
}
