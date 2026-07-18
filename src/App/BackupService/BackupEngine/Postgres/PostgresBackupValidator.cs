namespace BackupService;

/// <summary>
/// Mode-specific structural validation of a PostgreSQL backup, layered on top of the archive-wide
/// SHA-256 checksum verification performed by the <c>verify</c> command.
/// </summary>
internal sealed class PostgresBackupValidator(PostgresToolRunner runner)
{
    public Task ValidateAsync(string archive, BackupManifest manifest, CancellationToken cancellationToken)
        => manifest.Mode switch
        {
            PostgresBackupMode.Snapshot => ValidateSnapshotAsync(archive, cancellationToken),
            PostgresBackupMode.Full => ValidateFullAsync(archive, cancellationToken),
            PostgresBackupMode.WalArchive => ValidateWalArchiveAsync(archive, manifest),
            _ => throw new InvalidOperationException($"Unsupported backup mode '{manifest.Mode}'.")
        };

    private async Task ValidateSnapshotAsync(string archive, CancellationToken cancellationToken)
    {
        var postgresDir = Path.Combine(archive, "postgres");
        if (!Directory.Exists(postgresDir))
            throw new InvalidOperationException("Snapshot backup is missing its postgres directory.");

        // pg_restore --list reads the dump's table of contents without touching a server, so it
        // confirms each custom-format dump is structurally intact.
        foreach (var dump in Directory.EnumerateFiles(postgresDir, "*.dump").Order(StringComparer.Ordinal))
            await runner.RunAsync("pg_restore", ["--list", dump], cancellationToken: cancellationToken);
    }

    private async Task ValidateFullAsync(string archive, CancellationToken cancellationToken)
    {
        var baseDir = Path.Combine(archive, "postgres", PostgresFullBackup.BaseBackupDirName);
        if (!File.Exists(Path.Combine(baseDir, "backup_manifest")))
            throw new InvalidOperationException("Full backup is missing its backup_manifest.");

        // -n skips WAL parsing (WAL is inside pg_wal.tar.gz for -X stream backups). pg_verifybackup
        // validates the base against the manifest checksums (tar-format support requires PG 17+).
        await runner.RunAsync("pg_verifybackup", ["-n", baseDir], cancellationToken: cancellationToken);
    }

    private static async Task ValidateWalArchiveAsync(string archive, BackupManifest manifest)
    {
        _ = archive;
        var archiveDir = manifest.Components
            .FirstOrDefault(c => c.Name == "postgres")?.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("WAL-archive manifest does not record an archive directory.");

        if (!Directory.Exists(archiveDir))
            throw new InvalidOperationException($"WAL archive directory not found: {archiveDir}");

        var segments = Directory.EnumerateFiles(archiveDir)
            .Select(Path.GetFileName)
            .Where(name => name is { Length: 24 } && name.All(Uri.IsHexDigit))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        foreach (var segment in segments)
        {
            var sidecar = Path.Combine(archiveDir, segment + ".sha256");
            if (!File.Exists(sidecar))
                throw new InvalidOperationException($"Missing checksum sidecar for WAL segment {segment}.");

            var expected = (await File.ReadAllTextAsync(sidecar)).Trim();
            var actual = await ChecksumService.HashFileAsync(Path.Combine(archiveDir, segment));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Checksum mismatch for WAL segment {segment}.");
        }

        for (var i = 1; i < segments.Count; i++)
        {
            var expectedNext = PostgresWalArchive.NextWalSegment(segments[i - 1]);
            if (!string.Equals(segments[i], expectedNext, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"WAL archive gap: expected {expectedNext} after {segments[i - 1]} but found {segments[i]}.");
        }
    }
}
