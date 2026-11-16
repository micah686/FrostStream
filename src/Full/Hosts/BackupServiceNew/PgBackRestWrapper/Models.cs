using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupServiceNew.PgBackRestWrapper;

public enum BackupType { Full, Differential, Incremental }
public enum RecoveryMode { Consistency, Latest, Timestamp, RestorePoint, Lsn }
public enum RecoveryStage { FilesRestored, RecoveryPending, TargetReached, Validated }
public sealed record OutputLine(DateTimeOffset Timestamp, bool IsError, string Text);
public sealed record CommandSpec(string Executable, IReadOnlyList<string> Arguments, TimeSpan Timeout);
public sealed record CommandResult(Guid Id, CommandSpec Command, DateTimeOffset Started, DateTimeOffset Finished,
    int ExitCode, string Output, string Error, bool Cancelled = false, bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !Cancelled && !TimedOut;
}
public sealed class CommandFailedException(CommandResult result) : Exception($"{result.Command.Executable} failed (exit {result.ExitCode}, timeout {result.TimedOut}, cancelled {result.Cancelled}): {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)}")
{
    public CommandResult Result { get; } = result;
}
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec command, IProgress<OutputLine>? progress = null, CancellationToken cancellationToken = default);
}
public sealed record BackupInfo(string Label, string Type, DateTimeOffset Started, DateTimeOffset Finished,
    long DatabaseBytes, long RepositoryBytes, bool HasError, JsonElement Metadata);
public sealed record RepositoryInfo(DateTimeOffset ObservedAt, int StatusCode, string StatusMessage,
    IReadOnlyList<BackupInfo> Backups, JsonElement Metadata);
public sealed record BackupResult(BackupType RequestedType, BackupInfo Produced, CommandResult Command);
public sealed record CompatibilityReport(string PgBackRestVersion, string PostgresVersion, string PgCtlVersion,
    string PsqlVersion, bool VerifyAvailable, bool VerifySetAvailable);
public sealed record RestoreRequest
{
    public required string Destination { get; init; }
    public required string ConfirmStanza { get; init; }
    public required bool ConfirmServerStopped { get; init; }
    public RecoveryMode Mode { get; init; } = RecoveryMode.Consistency;
    public string? BackupLabel { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? Target { get; init; }
    public string Timeline { get; init; } = "latest";
    public bool Exclusive { get; init; }
    public bool Delta { get; init; }
    /// <summary>Existing independent preserved rollback directory; caller is responsible for its completeness.</summary>
    public string? PreservedRollbackDirectory { get; init; }
    public long MinimumFreeBytes { get; init; }
}
public sealed record RestoreReceipt(Guid Id, RestoreRequest Request, string BackupLabel, RecoveryStage Stage,
    DateTimeOffset UpdatedAt, string Evidence, string PostgresVersion);
public sealed record ScratchRequest(string RootDirectory, string? BackupLabel = null,
    IReadOnlyList<string>? ExpectedDatabases = null, IReadOnlyList<SqlAssertion>? Assertions = null);
public sealed record SqlAssertion(string Database, string Sql, string ExpectedOutput);
public sealed record ScratchResult(string Directory, RestoreReceipt Restore, IReadOnlyList<string> Checks,
    TimeSpan Duration, bool CleanedUp);
public sealed record HealthReport(DateTimeOffset ObservedAt, bool Healthy, RepositoryInfo? Repository,
    int WalReadySegments, long RepositoryFreeBytes, long DataFreeBytes, string? ArchiverStatistics,
    IReadOnlyList<string> Problems);
public sealed record WrapperOptions
{
    public required string Stanza { get; init; }
    public required string DataPath { get; init; }
    public required string RepositoryPath { get; init; }
    public required string StatePath { get; init; }
    public required string ConfigPath { get; init; }
    public string PgBackRest { get; init; } = "pgbackrest";
    public string Postgres { get; init; } = "postgres";
    public string PgCtl { get; init; } = "pg_ctl";
    public string Psql { get; init; } = "psql";
    public string PgDump { get; init; } = "pg_dump";
    public string SocketPath { get; init; } = "/var/run/postgresql";
    public int Port { get; init; } = 5432;
    public string DatabaseUser { get; init; } = "postgres";
    public int FullRetention { get; init; } = 4;
    public int DifferentialRetention { get; init; } = 14;
    public int CompressionLevel { get; init; } = 3;
    public int ProcessCount { get; init; } = 2;
    public bool Bundle { get; init; }
    public bool BlockIncremental { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaximumBackupAge { get; init; } = TimeSpan.FromDays(2);
    public int MaximumWalBacklog { get; init; } = 10;
    public long MinimumFreeBytes { get; init; } = 1024L * 1024 * 1024;
    /// <summary>Validates option values and ensures configured paths are safe and disjoint.</summary>
    internal void Validate()
    {
        if (!Regex.IsMatch(Stanza, "^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$")) throw new ArgumentException("Invalid stanza.");
        foreach (var path in new[] { DataPath, RepositoryPath, StatePath, ConfigPath, SocketPath }) Paths.Absolute(path);
        Paths.Disjoint(DataPath, RepositoryPath); Paths.Disjoint(DataPath, StatePath);
        Paths.Disjoint(RepositoryPath, StatePath);
        if (DatabaseUser.Any(char.IsControl) || string.IsNullOrWhiteSpace(DatabaseUser)) throw new ArgumentException("Invalid database user.");
        if (Paths.Within(ConfigPath, DataPath) || Paths.Within(ConfigPath, RepositoryPath)) throw new ArgumentException("Configuration must be outside data and repository.");
        if (FullRetention < 1 || DifferentialRetention < 1 || ProcessCount < 1 || CompressionLevel is < -7 or > 22 ||
            Port is < 1 or > 65535 || CommandTimeout <= TimeSpan.Zero || RecoveryTimeout <= TimeSpan.Zero ||
            MaximumBackupAge <= TimeSpan.Zero || MaximumWalBacklog < 0 || MinimumFreeBytes < 0)
            throw new ArgumentException("Invalid retention, tuning, timeout, port, or health threshold.");
        if (BlockIncremental && !Bundle) throw new ArgumentException("Block incremental storage requires bundling.");
    }
}
internal static class Paths
{
    /// <summary>Validates and normalizes a fully qualified non-root filesystem path.</summary>
    /// <param name="path">Path to normalize.</param>
    internal static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("A fully qualified path without control characters is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full)) throw new ArgumentException("Filesystem root is not a safe destination.");
        return full;
    }
    /// <summary>Determines whether one normalized path is equal to or nested beneath another.</summary>
    /// <param name="child">Candidate child path.</param><param name="parent">Candidate parent path.</param>
    internal static bool Within(string child, string parent) => Absolute(child) == Absolute(parent) ||
        Absolute(child).StartsWith(Absolute(parent) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    /// <summary>Requires two paths to be outside one another's directory trees.</summary>
    /// <param name="a">First path.</param><param name="b">Second path.</param>
    internal static void Disjoint(string a, string b)
    {
        if (Within(a, b) || Within(b, a)) throw new ArgumentException($"Paths must be disjoint: {a}, {b}");
    }
    /// <summary>Rejects symbolic links in the path and its existing parent chain.</summary>
    /// <param name="path">Path to inspect.</param>
    internal static void NoLinks(string path)
    {
        for (var current = new DirectoryInfo(Absolute(path)); current != null; current = current.Parent)
            if (current.LinkTarget != null) throw new IOException($"Symlink paths are refused: {current.FullName}");
    }
    /// <summary>Creates a directory when needed and verifies that it can be written safely.</summary>
    /// <param name="path">Directory to create and probe.</param>
    internal static void Writable(string path)
    {
        NoLinks(path);
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, $".wrapper-probe-{Guid.NewGuid():N}");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }
    /// <summary>Returns available free bytes on the filesystem containing a path.</summary>
    /// <param name="path">Path whose filesystem capacity is queried.</param>
    internal static long FreeBytes(string path) => new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
}
