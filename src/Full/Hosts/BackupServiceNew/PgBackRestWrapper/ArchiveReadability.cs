using System.Globalization;
using System.Text.RegularExpressions;

namespace BackupServiceNew.PgBackRestWrapper;

/// <summary>Readability evidence for the bounded WAL range needed by a backup.</summary>
public sealed record ArchiveReadability(DateTimeOffset ObservedAt, string Verdict, string Detail);

public sealed partial class PgBackRestClient
{
    /// <summary>Retrieves a bounded same-log WAL range through pgBackRest. This does not establish target reachability.</summary>
    public Task<ArchiveReadability> CheckArchiveReadabilityAsync(string generation, string? start, string? stop,
        CancellationToken cancellationToken = default) => Exclusive(async () =>
    {
        ArchiveReadability Result(string verdict, string detail) => new(DateTimeOffset.UtcNow, verdict, detail);
        if (!Regex.IsMatch(generation, @"\A\d+(?:\.\d+)?-\d+\z") || start is null || stop is null ||
            !Regex.IsMatch(start, @"\A[0-9A-F]{24}\z") || !Regex.IsMatch(stop, @"\A[0-9A-F]{24}\z"))
            return Result("Unknown", "Archive generation or WAL bounds are unavailable.");
        if (start[..16] != stop[..16] || string.CompareOrdinal(start, stop) > 0)
            return Result("Unknown", "WAL bounds span timelines or log files; this bounded check cannot establish the full range.");
        var first = Convert.ToUInt32(start[16..], 16);
        var last = Convert.ToUInt32(stop[16..], 16);
        if (last - first >= 256) return Result("Unknown", "WAL range exceeds the 256 segment check limit.");
        var root = Path.Combine(options.RepositoryPath, "archive", options.Stanza);
        Paths.NoLinks(root);
        if (!Directory.Exists(root)) return Result("Unknown", "Archive directory is unavailable.");
        var generations = Directory.EnumerateDirectories(root).Select(Path.GetFileName).ToArray();
        if (generations.Length != 1 || generations[0] != generation)
            return Result("Unknown", "Archive retrieval cannot be attributed safely to the pinned generation.");
        Paths.NoLinks(Path.Combine(root, generation));
        var temp = Path.Combine(options.StatePath, "wal-check-" + Guid.NewGuid().ToString("N"));
        Paths.Writable(temp);
        try
        {
            for (var n = first; ; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Paths.FreeBytes(temp) - options.MinimumFreeBytes < 1024L * 1024 * 1024)
                    return Result("Unknown", "Insufficient scratch space to retrieve WAL safely.");
                var name = start[..16] + n.ToString("X8", CultureInfo.InvariantCulture);
                var path = Path.Combine(temp, name);
                var command = await Run(options.PgBackRest, Arguments("archive-get", "--no-archive-async").Concat([name, path]).ToArray(), cancellationToken, requireSuccess: false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!command.Success || !File.Exists(path) || new FileInfo(path).Length == 0)
                    return Result("Failed", $"Could not read WAL segment {name} through pgBackRest (exit {command.ExitCode}, timeout {command.TimedOut}).");
                File.Delete(path);
                if (n == last) break;
            }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        return Result("Passed", $"Retrieved every WAL segment from {start} through {stop}. Coverage beyond backup consistency, timeline history and target reachability remain unproven.");
    }, cancellationToken);
}
