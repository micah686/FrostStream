using System.Globalization;

namespace BackupService;

/// <summary>
/// Continuous WAL archiving using the server-driven model: PostgreSQL's <c>archive_command</c>
/// invokes <c>wal-archive receive</c> for each completed segment, and recovery's
/// <c>restore_command</c> invokes <c>wal-archive restore</c>. The tool owns no long-running
/// process. <c>create --mode wal-archive</c> initializes the archive store and emits the server
/// settings to apply.
/// </summary>
internal sealed class PostgresWalArchive(PostgresOptions options) : IPostgresBackupStrategy
{
    /// <summary>Default segment count per logical WAL file (16 MB segments → 256 per log).</summary>
    private const uint DefaultSegmentsPerLog = 0x100;

    public const string ToolCommandPlaceholder = "dotnet /app/BackupService.dll";

    public PostgresBackupMode Mode => PostgresBackupMode.WalArchive;

    public Task<BackupComponent> BackupAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var archiveDir = ResolveArchiveDir(backupDirectory);
        Directory.CreateDirectory(archiveDir);

        // The archive store is a persistent sibling of the backup, so its accumulating segments are
        // intentionally excluded from this backup's checksum tree. Guidance goes to stderr; stdout
        // stays reserved for the backup directory path that callers parse.
        Console.Error.WriteLine($"WAL archive store initialized at: {archiveDir}");
        Console.Error.WriteLine("Apply these PostgreSQL settings (see 'wal-archive setup'), then reload/restart:");
        Console.Error.WriteLine(BuildServerConfig(archiveDir, options.ToolCommand ?? ToolCommandPlaceholder));

        return Task.FromResult(new BackupComponent("postgres", "wal-archive", [archiveDir]));
    }

    public string ResolveArchiveDir(string backupDirectory)
        => options.ArchiveDir
           ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupDirectory))!, "wal-archive");

    public static async Task<int> HandleAsync(CliOptions options)
    {
        var sub = options.Positional.FirstOrDefault();
        return sub switch
        {
            "receive" => await ReceiveAsync(options),
            "restore" => RestoreSegment(options),
            "setup" => Setup(options),
            _ => throw new InvalidOperationException(
                $"Unknown wal-archive subcommand '{sub}'. Expected receive|restore|setup.")
        };
    }

    /// <summary>Server <c>archive_command</c> target: <c>wal-archive receive %p %f --archive-dir &lt;dir&gt;</c>.</summary>
    private static async Task<int> ReceiveAsync(CliOptions options)
    {
        var archiveDir = options.Required("archive-dir");
        var source = options.Positional.ElementAtOrDefault(1)
                     ?? throw new InvalidOperationException("Missing WAL source path (%p).");
        var walName = options.Positional.ElementAtOrDefault(2)
                      ?? throw new InvalidOperationException("Missing WAL file name (%f).");

        Directory.CreateDirectory(archiveDir);
        var target = Path.Combine(archiveDir, walName);
        var checksum = await ChecksumService.HashFileAsync(source);

        if (File.Exists(target))
        {
            // archive_command must be idempotent for retries but fail on a differing segment.
            var existing = await ChecksumService.HashFileAsync(target);
            if (string.Equals(existing, checksum, StringComparison.Ordinal))
                return 0;
            throw new InvalidOperationException($"WAL segment already archived with different content: {walName}");
        }

        var temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        await using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite))
            stream.Flush(flushToDisk: true);
        File.Move(temp, target, overwrite: false);
        await File.WriteAllTextAsync(target + ".sha256", checksum);
        return 0;
    }

    /// <summary>Recovery <c>restore_command</c> target: <c>wal-archive restore %f %p --archive-dir &lt;dir&gt;</c>.</summary>
    private static int RestoreSegment(CliOptions options)
    {
        var archiveDir = options.Required("archive-dir");
        var walName = options.Positional.ElementAtOrDefault(1)
                      ?? throw new InvalidOperationException("Missing WAL file name (%f).");
        var dest = options.Positional.ElementAtOrDefault(2)
                   ?? throw new InvalidOperationException("Missing WAL destination path (%p).");

        var source = Path.Combine(archiveDir, walName);
        if (!File.Exists(source))
            return 1; // Non-zero tells recovery this segment is absent (normal end-of-WAL signal).

        File.Copy(source, dest, overwrite: true);
        return 0;
    }

    private static int Setup(CliOptions options)
    {
        var archiveDir = options.Required("archive-dir");
        var tool = options.Get("tool-command") ?? ToolCommandPlaceholder;
        Console.WriteLine(BuildServerConfig(archiveDir, tool));
        return 0;
    }

    private static string BuildServerConfig(string archiveDir, string tool)
        => $"""
            wal_level = replica
            archive_mode = on
            archive_command = '{tool} wal-archive receive %p %f --archive-dir {archiveDir}'
            max_wal_senders = 10
            """;

    /// <summary>
    /// Computes the WAL segment name that should immediately follow <paramref name="name"/>,
    /// assuming the default 16 MB segment size.
    /// </summary>
    public static string NextWalSegment(string name)
    {
        var timeline = name[..8];
        var log = uint.Parse(name[8..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var segment = uint.Parse(name[16..24], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        segment++;
        if (segment >= DefaultSegmentsPerLog)
        {
            segment = 0;
            log++;
        }

        return string.Create(24, (timeline, log, segment), static (span, state) =>
        {
            state.timeline.CopyTo(span);
            state.log.TryFormat(span[8..16], out _, "X8", CultureInfo.InvariantCulture);
            state.segment.TryFormat(span[16..24], out _, "X8", CultureInfo.InvariantCulture);
        });
    }
}
