using System.Security.Cryptography;

namespace BackupService.PgBackRest;

/// <summary>
/// Pairs each pgBackRest backup with an OpenBao KV export: pgBackRest cannot cover the secret
/// store, so every successful backup writes `openbao/&lt;label&gt;.json` (+ `.sha256` sidecar)
/// under the backup root. Exports whose label has been expired out of the repository are pruned
/// after each backup so retention stays aligned with pgBackRest's.
/// </summary>
internal class OpenBaoPairing(BackupServiceOptions options, ILogger<OpenBaoPairing> logger)
{
    private string ExportDirectory => Path.Combine(Path.GetFullPath(options.Directory), "openbao");

    public virtual async Task ExportForBackupAsync(string label, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ExportDirectory);
        var file = ExportFile(label);
        await new OpenBaoBackup().ExportToFileAsync(OpenBaoOptions.From(options), file);
        await File.WriteAllTextAsync(
            file + ".sha256",
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, cancellationToken))),
            cancellationToken);
    }

    public virtual bool HasExport(string label) => File.Exists(ExportFile(label));

    /// <summary>Restores the export paired with <paramref name="label"/> into OpenBao.</summary>
    public async Task RestoreAsync(string label, CancellationToken cancellationToken)
    {
        var file = ExportFile(label);
        if (!File.Exists(file))
            throw new FileNotFoundException($"No OpenBao export exists for backup {label}.", file);

        var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
        var sidecar = file + ".sha256";
        if (File.Exists(sidecar))
        {
            var expected = (await File.ReadAllTextAsync(sidecar, cancellationToken)).Trim();
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"OpenBao export for {label} failed its checksum; refusing to restore it.");
        }

        var export = System.Text.Json.JsonSerializer.Deserialize<OpenBaoKvExport>(bytes, BackupJson.Options)
                     ?? throw new InvalidOperationException($"OpenBao export for {label} could not be parsed.");
        await new OpenBaoBackup().RestoreAsync(OpenBaoOptions.From(options), export);
    }

    /// <summary>Deletes exports whose backup label no longer exists in the repository.</summary>
    public virtual void PruneExcept(IReadOnlyCollection<string> liveLabels)
    {
        if (!Directory.Exists(ExportDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(ExportDirectory, "*.json"))
        {
            var label = Path.GetFileNameWithoutExtension(file);
            if (liveLabels.Contains(label))
                continue;
            logger.LogInformation("Pruning OpenBao export for expired backup {Label}.", label);
            File.Delete(file);
            var sidecar = file + ".sha256";
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }
    }

    private string ExportFile(string label) => Path.Combine(ExportDirectory, label + ".json");
}
