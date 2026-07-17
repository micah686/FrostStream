using System.Text.Json;
using Shared.Backups;

namespace BackupService;

internal sealed class BackupArchiveCatalog(BackupJobStore store, ILogger<BackupArchiveCatalog> logger)
{
    public IReadOnlyList<BackupArchiveDto> List()
    {
        if (!Directory.Exists(store.Archives))
            return [];

        var archives = new List<BackupArchiveDto>();
        foreach (var manifestPath in Directory.EnumerateFiles(store.Archives, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                archives.Add(new BackupArchiveDto(
                    Path.GetDirectoryName(manifestPath)!,
                    root.TryGetProperty("createdAtUtc", out var created) && created.TryGetDateTimeOffset(out var createdAt) ? createdAt : null,
                    root.TryGetProperty("mediaIncluded", out var media) && media.GetBoolean(),
                    root.TryGetProperty("schemaVersion", out var schema) ? schema.GetInt32() : 0,
                    root.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "Snapshot" : "Snapshot"));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ignoring unreadable backup manifest {ManifestPath}.", manifestPath);
            }
        }

        return archives.OrderByDescending(x => x.CreatedAt).ToArray();
    }
}
