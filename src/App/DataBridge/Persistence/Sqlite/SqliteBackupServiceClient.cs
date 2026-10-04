using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, BackupJobDto> jobs = new();

    public SqliteBackupServiceClient(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        this.connectionFactory = connectionFactory;
        var configuredDirectory = configuration["Backup:Directory"]
            ?? configuration["Persistence:Sqlite:BackupPath"]
            ?? DefaultBackupDirectory;
        backupDirectory = Path.GetFullPath(configuredDirectory, environment.ContentRootPath);
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
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var source = connectionFactory.OpenConnection(cancellationToken))
            using (var destination = OpenDestination(temporaryPath))
            {
                source.BackupDatabase(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyFileAsync(temporaryPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);

            var job = new BackupJobDto(
                Guid.NewGuid(), "backup", "full", "completed", request.Name ?? Path.GetFileNameWithoutExtension(fileName),
                fileName, null, createdAt, DateTimeOffset.UtcNow, [$"Created and verified {fileName}."]);
            jobs[job.JobId] = job;
            return job;
        }
        catch
        {
            TryDelete(temporaryPath);
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
                file.Length, file.Length, null, null, false, false))
            .ToArray();
        return Task.FromResult(new BackupRepositoryDto(true, null, backups, new PitrWindowDto(null, null),
            new SqliteConnectionStringBuilder(connectionFactory.ConnectionString).DataSource, backupDirectory));
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
        await VerifyFileAsync(targetPath, cancellationToken);
        var label = Path.GetFileName(targetPath);
        var job = new BackupJobDto(Guid.NewGuid(), "verify-quick", null, "completed", label, label, null,
            started, DateTimeOffset.UtcNow, [$"SQLite integrity check passed for {label}."]);
        jobs[job.JobId] = job;
        return job;
    }

    private static SqliteConnection OpenDestination(string path)
    {
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

    private async Task VerifyFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite backup integrity check failed: {result ?? "no result"}.");
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
