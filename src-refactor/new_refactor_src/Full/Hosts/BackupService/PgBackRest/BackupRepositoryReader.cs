using Shared.Backups;

namespace BackupService.PgBackRest;

/// <summary>
/// Projects `pgbackrest info` into the shared repository DTO used by the internal REST surface
/// and the restore wizard: backups with their annotations/sizes/WAL ranges, OpenBao pairing
/// presence, and the PITR window (oldest usable full backup → approximately now, since WAL
/// archiving is continuous while the repository is healthy).
/// </summary>
internal sealed class BackupRepositoryReader(PgBackRestRunner runner, OpenBaoPairing pairing)
{
    public async Task<BackupRepositoryDto> ReadAsync(CancellationToken cancellationToken)
    {
        PgBackRestStanzaInfo? info;
        try
        {
            info = await runner.InfoAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new BackupRepositoryDto(false, ex.Message, [], new PitrWindowDto(null, null));
        }

        if (info is null)
            return new BackupRepositoryDto(false, "The pgBackRest stanza has not been created yet.", [], new PitrWindowDto(null, null));

        var backups = info.Backup
            .Where(b => b.Label is not null)
            .Select(b => new BackupInfoDto(
                b.Label!,
                b.Type ?? "full",
                b.AnnotatedName,
                b.StartedAt,
                b.CompletedAt,
                b.Info?.Size,
                b.Info?.Repository?.Delta,
                b.WalRange?.Start,
                b.WalRange?.Stop,
                b.Error ?? false,
                pairing.HasExport(b.Label!)))
            .ToArray();

        var earliest = backups
            .Where(b => b.Type == "full" && !b.HasError)
            .Select(b => b.CompletedAt)
            .Where(x => x is not null)
            .DefaultIfEmpty(null)
            .Min();
        var window = new PitrWindowDto(earliest, earliest is null ? null : DateTimeOffset.UtcNow);

        return new BackupRepositoryDto(
            info.Status?.Code == 0,
            info.Status?.Message,
            backups,
            window);
    }
}
