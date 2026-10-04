using System.Collections.Concurrent;
using DataBridge.Persistence.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shared.Backups;

namespace DataBridge.Persistence.Sqlite;

/// <summary>Creates verified, online snapshots of Lite's application database.</summary>
public sealed class SqliteBackupServiceClient : IBackupServiceClient
{
    public const string DefaultBackupDirectory = "/data/backups";
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly string backupDirectory;
    private readonly string keyRingPath;
    private readonly ConcurrentDictionary<Guid, BackupJobDto> jobs = new();

    public SqliteBackupServiceClient(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        LocalSecretStoreOptions secrets)
    {
        this.connectionFactory = connectionFactory;
        var configuredDirectory = configuration["Backup:Directory"]
            ?? configuration["Persistence:Sqlite:BackupPath"]
            ?? DefaultBackupDirectory;
        backupDirectory = Path.GetFullPath(configuredDirectory, environment.ContentRootPath);
        keyRingPath = Path.TrimEndingDirectorySeparator(secrets.KeyRingPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(backupDirectory, keyRingPath, comparison) ||
            backupDirectory.StartsWith(keyRingPath + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("The SQLite backup directory must be separate from and outside the local encryption key ring.");
    }

    public async Task<BackupJobDto> CreateAsync(CreateBackupJobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Type is not (null or "full"))
            throw new NotSupportedException("Lite supports full SQLite snapshots only; differential and point-in-time backups are not supported.");

        Directory.CreateDirectory(backupDirectory);
        var createdAt = DateTimeOffset.UtcNow;
        var fileName = $"{createdAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.sqlite";
        var finalPath = Path.Combine(backupDirectory, fileName);
        var temporaryPath = Path.Combine(backupDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        var temporaryKeys = temporaryPath + SqliteRecoveryArtifacts.KeysSuffix;
        var temporaryManifest = temporaryPath + SqliteRecoveryArtifacts.ManifestSuffix;
        var finalKeys = finalPath + SqliteRecoveryArtifacts.KeysSuffix;
        var finalManifest = finalPath + SqliteRecoveryArtifacts.ManifestSuffix;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var source = connectionFactory.OpenConnection(cancellationToken))
            using (var destination = OpenDestination(temporaryPath))
            {
                source.BackupDatabase(destination);
                using var journal = destination.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                if (!string.Equals(journal.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SQLite recovery snapshots must be standalone database files.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Keys used to encrypt committed SQLite documents are persisted before those writes.
            // Copy after the database snapshot, then prove every snapshotted secret is decryptable.
            SqliteRecoveryArtifacts.CopyKeys(keyRingPath, temporaryKeys, cancellationToken);
            await SqliteRecoveryArtifacts.WriteManifestAsync(temporaryPath, temporaryKeys, temporaryManifest, cancellationToken);
            await SqliteRecoveryArtifacts.VerifyAsync(temporaryPath, temporaryKeys, temporaryManifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryKeys, finalKeys);
            File.Move(temporaryManifest, finalManifest);
            // The database is the commit marker: repository listings only see complete recovery sets.
            File.Move(temporaryPath, finalPath);

            var job = new BackupJobDto(
                Guid.NewGuid(), "backup", "full", "completed", request.Name ?? Path.GetFileNameWithoutExtension(fileName),
                fileName, null, createdAt, DateTimeOffset.UtcNow, [$"Created and verified {fileName}, its complete companion key ring, and recovery manifest."]);
            jobs[job.JobId] = job;
            return job;
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryPath + "-wal");
            TryDelete(temporaryPath + "-shm");
            TryDelete(temporaryPath + "-journal");
            TryDelete(temporaryManifest);
            TryDeleteDirectory(temporaryKeys);
            TryDelete(finalManifest);
            TryDeleteDirectory(finalKeys);
            throw;
        }
    }

    public Task<IReadOnlyList<BackupJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<BackupJobDto> result = jobs.Values.OrderByDescending(job => job.CreatedAt).ToArray();
        return Task.FromResult(result);
    }

    public Task<BackupJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    public Task<BackupRepositoryDto> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(backupDirectory);
        var backups = Directory.EnumerateFiles(backupDirectory, "*.sqlite", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new BackupInfoDto(
                file.Name, "full", Path.GetFileNameWithoutExtension(file.Name),
                new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Length, file.Length, null, null,
                !HasRecoveryArtifacts(file.FullName), false, HasRecoveryArtifacts(file.FullName)))
            .ToArray();
        return Task.FromResult(new BackupRepositoryDto(true,
            backups.Any(backup => backup.HasError) ? "Some snapshots lack a companion key ring or recovery manifest. Preserve the current database and keys before attempting recovery." : null,
            backups, new PitrWindowDto(null, null),
            new SqliteConnectionStringBuilder(connectionFactory.ConnectionString).DataSource, backupDirectory, keyRingPath));
    }

    public async Task<BackupJobDto> VerifyAsync(VerifyBackupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Deep)
            throw new NotSupportedException("Lite supports SQLite integrity verification only; deep restore verification is not supported.");

        Directory.CreateDirectory(backupDirectory);
        var targetPath = request.Label is null
            ? Directory.EnumerateFiles(backupDirectory, "*.sqlite", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : ResolveBackupPath(request.Label);
        if (targetPath is null || !File.Exists(targetPath))
            throw new FileNotFoundException("No SQLite backup was found to verify.", request.Label);

        var started = DateTimeOffset.UtcNow;
        await SqliteRecoveryArtifacts.VerifyAsync(targetPath, targetPath + SqliteRecoveryArtifacts.KeysSuffix,
            targetPath + SqliteRecoveryArtifacts.ManifestSuffix, cancellationToken);
        var label = Path.GetFileName(targetPath);
        var job = new BackupJobDto(Guid.NewGuid(), "verify-quick", null, "completed", label, label, null,
            started, DateTimeOffset.UtcNow, [$"Database integrity, recovery checksums, and encrypted-secret decryption passed for {label}."]);
        jobs[job.JobId] = job;
        return job;
    }

    private static SqliteConnection OpenDestination(string path)
    {
        using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        SqliteRecoveryArtifacts.MakePrivateFile(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool HasRecoveryArtifacts(string path)
        => Directory.Exists(path + SqliteRecoveryArtifacts.KeysSuffix) && File.Exists(path + SqliteRecoveryArtifacts.ManifestSuffix);

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string ResolveBackupPath(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || !string.Equals(Path.GetFileName(label), label, StringComparison.Ordinal) ||
            !label.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup label must be a SQLite backup filename.", nameof(label));
        return Path.Combine(backupDirectory, label);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
