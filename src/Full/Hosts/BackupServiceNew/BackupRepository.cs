using System.Text.Json;
using BackupService.PgBackRest;
using BackupServiceNew.PgBackRestWrapper;
using Shared.Backups;

namespace BackupServiceNew;

internal sealed class BackupRepository(PgBackRestClient client, OpenBaoPairing openBao, WrapperOptions options, LogicalBackups logical)
{
    // pgBackRest info status codes for an empty or uninitialized stanza.
    private const int MissingStanzaPath = 1;
    private const int NoValidBackups = 2;
    private const int MissingStanzaData = 3;

    public async Task PrepareBackupAsync(string type, Action<string> report, CancellationToken ct)
    {
        var repository = await client.GetRepositoryAsync(ct);
        if (type != BackupTypes.Full
            && (repository.StatusCode is MissingStanzaPath or MissingStanzaData or NoValidBackups
                || repository.StatusCode == 0 && !HasFullBase(repository)))
        {
            throw new InvalidOperationException("Create a full backup first. Differential and incremental backups require a usable full backup for this PostgreSQL cluster.");
        }

        if (repository.StatusCode is MissingStanzaPath or MissingStanzaData)
        {
            report($"Initializing pgBackRest stanza '{options.Stanza}' and checking WAL archiving.");
            await client.BootstrapAsync(ct);
            repository = await client.GetRepositoryAsync(ct);
        }

        if (repository.StatusCode is not (0 or NoValidBackups))
            throw new InvalidOperationException($"pgBackRest stanza '{options.Stanza}': {repository.StatusMessage}.");
    }

    public async Task<BackupRepositoryDto> ReadAsync(CancellationToken ct)
    {
        var repository = await client.GetRepositoryAsync(ct);
        var status = repository.StatusCode is MissingStanzaPath or MissingStanzaData or NoValidBackups
            ? "Create a full backup to start a backup chain."
            : repository.StatusMessage;
        return new BackupRepositoryDto(repository.StatusCode == 0, status,
            repository.Backups.Select(Describe).Concat(logical.List()).GroupBy(b => b.Label).Select(group => group.Last()).OrderBy(b => b.StartedAt).ToArray(), new PitrWindowDto(null, null),
            options.DataPath, options.RepositoryPath);
    }

    public BackupInfoDto Describe(BackupInfo backup)
    {
        var metadata = backup.Metadata;
        return new BackupInfoDto(backup.Label, backup.Type, ReadString(metadata, "annotation", "name"),
            backup.Started, backup.Finished, backup.DatabaseBytes, backup.RepositoryBytes,
            ReadString(metadata, "archive", "start"), ReadString(metadata, "archive", "stop"), backup.HasError,
            openBao.HasCompleteExport(backup.Label))
        {
            Dependencies = metadata.TryGetProperty("reference", out var references) && references.ValueKind == JsonValueKind.Array
                ? references.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : []
        };
    }

    private static bool HasFullBase(RepositoryInfo repository)
    {
        // info lists database history oldest to newest. A full from before a stanza upgrade
        // cannot be used as a base for the current generation.
        var current = repository.Metadata.GetProperty("db").EnumerateArray().Last().GetProperty("id").GetInt32();
        return repository.Backups.Any(backup => backup.Type == BackupTypes.Full && !backup.HasError
            && backup.Metadata.GetProperty("database").GetProperty("id").GetInt32() == current);
    }

    private static string? ReadString(JsonElement metadata, string section, string key)
        => metadata.TryGetProperty(section, out var parent) && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(key, out var value) ? value.GetString() : null;
}
