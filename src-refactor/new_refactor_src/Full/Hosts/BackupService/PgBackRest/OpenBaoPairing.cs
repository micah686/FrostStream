using System.Security.Cryptography;

namespace BackupService.PgBackRest;

/// <summary>
/// Pairs each pgBackRest backup with OpenBao backups: pgBackRest cannot cover the secret store,
/// so every successful backup writes `openbao/&lt;label&gt;.raft-snapshot` (an online snapshot of
/// OpenBao's actual storage — the `openbao-data` volume, see <see cref="OpenBaoRaftSnapshot"/>)
/// plus `openbao/&lt;label&gt;.json` (a human-readable KV-only export, kept as a fallback), each
/// with a `.sha256` sidecar. Exports whose label has been expired out of the repository are
/// pruned after each backup so retention stays aligned with pgBackRest's.
/// </summary>
internal class OpenBaoPairing(BackupServiceOptions options, ILogger<OpenBaoPairing> logger)
{
    private const string SnapshotExtension = ".raft-snapshot";

    private string ExportDirectory => Path.Combine(Path.GetFullPath(options.Directory), "openbao");

    public virtual async Task ExportForBackupAsync(string label, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ExportDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ExportDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var openBaoOptions = OpenBaoOptions.From(options);
        var kvFile = ExportFile(label);
        await new OpenBaoBackup().ExportToFileAsync(openBaoOptions, kvFile);
        MakePrivate(kvFile);
        await WriteChecksumAsync(kvFile, cancellationToken);

        var snapshotFile = SnapshotFile(label);
        await new OpenBaoRaftSnapshot().SaveToFileAsync(openBaoOptions, snapshotFile, cancellationToken);
        MakePrivate(snapshotFile);
        await WriteChecksumAsync(snapshotFile, cancellationToken);
    }

    public virtual bool HasExport(string label) => File.Exists(ExportFile(label));
    public virtual bool HasSnapshotExport(string label) => File.Exists(SnapshotFile(label));

    /// <summary>Restores the KV-only export paired with <paramref name="label"/> into OpenBao. Prefer <see cref="RestoreSnapshotAsync"/> — this is a fallback if the snapshot restore can't be used.</summary>
    public virtual async Task RestoreAsync(string label, CancellationToken cancellationToken)
    {
        var file = ExportFile(label);
        if (!File.Exists(file))
            throw new FileNotFoundException($"No OpenBao export exists for backup {label}.", file);

        var bytes = await ReadVerifiedAsync(file, cancellationToken);
        var export = System.Text.Json.JsonSerializer.Deserialize<OpenBaoKvExport>(bytes, BackupJson.Options)
                     ?? throw new InvalidOperationException($"OpenBao export for {label} could not be parsed.");
        await new OpenBaoBackup().RestoreAsync(OpenBaoOptions.From(options), export);
    }

    /// <summary>
    /// Restores the Raft snapshot paired with <paramref name="label"/> — OpenBao's complete
    /// storage as it was at backup time, not just the KV mount. Requires OpenBao to already be
    /// unsealed and reachable; replaces its entire storage, including auth backends and the token
    /// store, with the snapshot's contents.
    /// </summary>
    public virtual async Task RestoreSnapshotAsync(string label, CancellationToken cancellationToken)
    {
        var file = SnapshotFile(label);
        if (!File.Exists(file))
            throw new FileNotFoundException($"No OpenBao snapshot exists for backup {label}.", file);

        // Verify the checksum up front rather than inside RestoreFromFileAsync: the snapshot
        // endpoint streams the file directly from disk, so corruption must be caught before that
        // stream opens, not after a partial upload.
        await VerifyChecksumAsync(file, cancellationToken);
        await new OpenBaoRaftSnapshot().RestoreFromFileAsync(OpenBaoOptions.From(options), file, cancellationToken);
    }

    /// <summary>Deletes exports (KV + snapshot) whose backup label no longer exists in the repository.</summary>
    public virtual void PruneExcept(IReadOnlyCollection<string> liveLabels)
    {
        if (!Directory.Exists(ExportDirectory))
            return;

        // KV and snapshot exports are written together but not atomically together, so derive
        // candidate labels from either file type rather than assuming one implies the other.
        var candidateLabels = Directory.EnumerateFiles(ExportDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Concat(Directory.EnumerateFiles(ExportDirectory, "*" + SnapshotExtension)
                .Select(f => Path.GetFileName(f)[..^SnapshotExtension.Length]))
            .Where(label => !string.IsNullOrEmpty(label))
            .Distinct(StringComparer.Ordinal);

        foreach (var label in candidateLabels)
        {
            if (liveLabels.Contains(label!))
                continue;
            logger.LogInformation("Pruning OpenBao export for expired backup {Label}.", label);
            DeleteWithChecksum(ExportFile(label!));
            DeleteWithChecksum(SnapshotFile(label!));
        }
    }

    private string ExportFile(string label) => Path.Combine(ExportDirectory, ValidateLabel(label) + ".json");
    private string SnapshotFile(string label) => Path.Combine(ExportDirectory, ValidateLabel(label) + SnapshotExtension);

    private static string ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label is "." or ".." || label.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Backup label must be a filename component.", nameof(label));
        return label;
    }
    private static void MakePrivate(string file)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task WriteChecksumAsync(string file, CancellationToken cancellationToken)
        => await File.WriteAllTextAsync(
            file + ".sha256",
            await HashFileAsync(file, cancellationToken),
            cancellationToken);

    private static async Task<string> HashFileAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<byte[]> ReadVerifiedAsync(string file, CancellationToken cancellationToken)
    {
        await VerifyChecksumAsync(file, cancellationToken);
        return await File.ReadAllBytesAsync(file, cancellationToken);
    }

    private static async Task VerifyChecksumAsync(string file, CancellationToken cancellationToken)
    {
        var sidecar = file + ".sha256";
        if (!File.Exists(sidecar))
            throw new InvalidOperationException($"'{Path.GetFileName(file)}' has no checksum; refusing to restore an unverified recovery artifact.");
        var expected = (await File.ReadAllTextAsync(sidecar, cancellationToken)).Trim();
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{Path.GetFileName(file)}' failed its checksum; refusing to use it.");
    }

    private static void DeleteWithChecksum(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
        var sidecar = file + ".sha256";
        if (File.Exists(sidecar))
            File.Delete(sidecar);
    }
}
