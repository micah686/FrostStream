using System.Globalization;
using System.Text.RegularExpressions;

namespace BackupServiceNew.PgBackRestWrapper;

public sealed record ArchiveSegmentEvidence(string Name, bool Readable, long? Bytes, string? Error);
public sealed record ArchiveHistoryEvidence(string Name, bool Readable, string? Contents, string? Error);
public sealed record ArchiveGap(string Timeline, string FirstMissing, string LastMissing);
public sealed record ArchiveInspection(DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<ArchiveSegmentEvidence> Segments, IReadOnlyList<ArchiveHistoryEvidence> Histories,
    IReadOnlyList<ArchiveGap> Gaps, IReadOnlyList<string> Warnings);

public sealed partial class PgBackRestClient
{
    /// <summary>Inspect listed archived WAL using pgBackRest retrieval. Gaps are reported only within observed timeline bounds.</summary>
    /// <summary>Retrieves listed archived WAL files and reports readability, observed gaps, and caveats.</summary>
    /// <param name="cancellationToken">Token used to cancel listing and retrieval.</param>
    public Task<ArchiveInspection> InspectArchiveAsync(CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        var started = DateTimeOffset.UtcNow;
        // This wrapper supports one local repository. Enumerating it avoids the command
        // runner's bounded stdout tail and never materializes a recursive text listing.
        var archiveRoot = Path.Combine(options.RepositoryPath, "archive", options.Stanza);
        Paths.NoLinks(archiveRoot);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var generations = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        var directories = new Stack<string>();
        directories.Push(archiveRoot);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > options.ArchiveInspectionEntryLimit)
                    throw new IOException("Archive inspection entry budget exceeded. Increase ArchiveInspectionEntryLimit to inspect this repository.");
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Archive inspection refuses symlink entries: {entry}");
                if (Directory.Exists(entry))
                {
                    directories.Push(entry);
                    continue;
                }
                var relative = Path.GetRelativePath(archiveRoot, entry);
                var generation = relative.Split(Path.DirectorySeparatorChar)[0];
                var leaf = Path.GetFileName(entry);
                var match = Regex.Match(leaf, @"^([0-9A-F]{24})-[0-9a-fA-F]{40}(?:\..+)?$");
                if (!match.Success)
                    match = Regex.Match(leaf, @"^([0-9A-F]{8}\.history)(?:\..+)?$");
                if (!match.Success) continue;
                generations.Add(generation);
                if (generations.Count > 1)
                    throw new InvalidOperationException("Multiple archive generations found; inspection cannot safely associate files with a single PostgreSQL cluster.");
                names.Add(match.Groups[1].Value);
            }
        }
        var segments = new List<ArchiveSegmentEvidence>(); var histories = new List<ArchiveHistoryEvidence>();
        var warnings = new List<string>();
        var temp = Path.Combine(options.StatePath, "archive-inspection-" + Guid.NewGuid().ToString("N"));
        Paths.Writable(temp);
        try
        {
            long historyBytes = 0;
            foreach (var batch in names.Chunk(options.ArchiveInspectionBatchSize))
            foreach (var name in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Reserve headroom for one maximum-size PostgreSQL WAL segment.
                if (Paths.FreeBytes(temp) - options.MinimumFreeBytes < 1024L * 1024 * 1024)
                    throw new IOException("Insufficient free space for archive inspection.");
                var path = Path.Combine(temp, name);
                var result = await Run(options.PgBackRest, Arguments("archive-get", "--no-archive-async").Concat([name, path]).ToArray(), cancellationToken, requireSuccess: false);
                cancellationToken.ThrowIfCancellationRequested();
                var exists = result.Success && File.Exists(path);
                var error = exists ? null : $"Retrieval failed (exit {result.ExitCode}, timeout {result.TimedOut}): {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)}";
                if (error?.Length > 4096) error = error[..4096];
                if (name.EndsWith(".history", StringComparison.Ordinal))
                {
                    string? contents = null;
                    if (exists && new FileInfo(path).Length <= 1024 * 1024 &&
                        new FileInfo(path).Length <= options.ArchiveInspectionHistoryBytes - historyBytes)
                    {
                        historyBytes += new FileInfo(path).Length;
                        contents = await File.ReadAllTextAsync(path, cancellationToken);
                    }
                    else if (exists) { exists = false; error = "History content exceeds the per-file or total inspection budget."; }
                    histories.Add(new(name, exists, contents, error));
                }
                else segments.Add(new(name, exists, exists ? new FileInfo(path).Length : null, error));
                File.Delete(path);
            }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        var sizes = segments.Where(s => s.Readable).Select(s => s.Bytes!.Value).Distinct().ToArray();
        var gaps = new List<ArchiveGap>();
        if (sizes.Length != 1 || sizes[0] < 1024 * 1024 || sizes[0] > 1024L * 1024 * 1024 || (sizes[0] & (sizes[0] - 1)) != 0)
            warnings.Add("WAL segment size could not be established unambiguously; gaps cannot be enumerated.");
        else
        {
            var perLog = (1L << 32) / sizes[0];
            foreach (var group in segments.GroupBy(s => s.Name[..8]))
            {
                var numbers = group.Select(s => (Name: s.Name, Log: Convert.ToInt64(s.Name.Substring(8, 8), 16), Seg: Convert.ToInt64(s.Name.Substring(16, 8), 16))).OrderBy(x => x.Log).ThenBy(x => x.Seg).ToArray();
                if (numbers.Any(x => x.Seg >= perLog)) { warnings.Add($"Invalid segment numbering on timeline {group.Key}."); continue; }
                for (var i = 1; i < numbers.Length; i++)
                {
                    var previous = numbers[i - 1].Log * perLog + numbers[i - 1].Seg;
                    var current = numbers[i].Log * perLog + numbers[i].Seg;
                    if (current <= previous + 1) continue;
                    string Format(long number) => group.Key + (number / perLog).ToString("X8", CultureInfo.InvariantCulture) + (number % perLog).ToString("X8", CultureInfo.InvariantCulture);
                    gaps.Add(new(group.Key, Format(previous + 1), Format(current - 1)));
                }
            }
        }
        foreach (var timeline in segments.Select(s => s.Name[..8]).Distinct().Where(t => t != "00000001"))
            if (!histories.Any(h => h.Name == timeline + ".history" && h.Readable)) warnings.Add($"Timeline {timeline} history is missing or unreadable.");
        warnings.Add("Listing and retrieval are a point-in-time observation; timeline branches, backup reachability and timestamps are not proven. Unreadable files are not classified as missing.");
        var report = new ArchiveInspection(started, DateTimeOffset.UtcNow, segments, histories, gaps, warnings);
        await reports.WriteAsync($"archive-inspection-{Guid.NewGuid():N}.json", report);
        return report;
    }, cancellationToken);
}
