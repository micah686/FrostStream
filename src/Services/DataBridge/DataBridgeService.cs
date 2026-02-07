using System.IO.Hashing;
using DataBridge.Data;
using FluentStorage;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Entities;
using Shared.Messages;

namespace DataBridge;

/// <summary>
/// Background service that handles storage configuration requests, file ingestion verification,
/// and movie query endpoints.
///
/// Horizontally scalable: Multiple instances can run concurrently using NATS queue groups.
/// </summary>
public class DataBridgeService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<DataBridgeService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;

    private const int MaxRetryAttempts = 3;

    private string StagingPath => _configuration["DataBridge:StagingPath"] ?? "../testing_data";

    public DataBridgeService(
        IMessageBus messageBus,
        ILogger<DataBridgeService> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory)
    {
        _messageBus = messageBus;
        _logger = logger;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataBridge service starting");

        Directory.CreateDirectory(StagingPath);

        // Storage config requests (request/reply)
        await _messageBus.SubscribeAsync<StorageConfigRequest>(
            Subjects.StorageConfig,
            HandleStorageConfigRequestAsync,
            queueGroup: "databridge-config",
            cancellationToken: stoppingToken);

        _logger.LogInformation("DataBridge subscribed to {Subject} (queue: databridge-config)",
            Subjects.StorageConfig);

        // File ingested events
        await _messageBus.SubscribeAsync<FileIngestedEvent>(
            Subjects.FileIngested,
            HandleFileIngestedEventAsync,
            queueGroup: "databridge-processors",
            cancellationToken: stoppingToken);

        _logger.LogInformation("DataBridge subscribed to {Subject} (queue: databridge-processors)",
            Subjects.FileIngested);

        // Movie query (request/reply)
        await _messageBus.SubscribeAsync<MovieQueryRequest>(
            Subjects.MovieQuery,
            HandleMovieQueryAsync,
            queueGroup: "databridge-query",
            cancellationToken: stoppingToken);

        _logger.LogInformation("DataBridge subscribed to {Subject} (queue: databridge-query)",
            Subjects.MovieQuery);

        // Movie get by ID (request/reply)
        await _messageBus.SubscribeAsync<MovieGetRequest>(
            Subjects.MovieGet,
            HandleMovieGetAsync,
            queueGroup: "databridge-query",
            cancellationToken: stoppingToken);

        _logger.LogInformation("DataBridge subscribed to {Subject} (queue: databridge-query)",
            Subjects.MovieGet);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleStorageConfigRequestAsync(IMessageContext<StorageConfigRequest> context)
    {
        var request = context.Message;
        _logger.LogInformation("Received storage config request for job {JobId} from worker {WorkerId}",
            request.JobId, request.WorkerId);

        var fullStagingPath = Path.GetFullPath(StagingPath);
        var response = new StorageConfigResponse
        {
            Method = StorageMethod.PosixLocal,
            ConnectionString = $"disk://path={fullStagingPath}"
        };

        _logger.LogInformation("Responding to job {JobId} with storage method {Method}, connection: {Connection}",
            request.JobId, response.Method, response.ConnectionString);

        await context.RespondAsync(response);
    }

    private async Task HandleFileIngestedEventAsync(IMessageContext<FileIngestedEvent> context)
    {
        var evt = context.Message;
        _logger.LogInformation(
            "Received file ingested event for job {JobId} from worker {WorkerId}. File: {Path}, Size: {Size} bytes, XxHash: {Hash}",
            evt.JobId, evt.WorkerId, evt.StagedPath, evt.FileSizeBytes, evt.XxHash);

        Guid? movieId = null;

        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation("Job {JobId}: Verification attempt {Attempt}/{Max}",
                    evt.JobId, attempt, MaxRetryAttempts);

                // 1. Open staged file via FluentStorage and verify hash
                using var storage = StorageFactory.Blobs.FromConnectionString(evt.StorageConnectionString);

                await using var fileStream = await storage.OpenReadAsync(evt.StagedPath);
                if (fileStream is null)
                    throw new FileNotFoundException($"Staged file not found: {evt.StagedPath}");

                // 2. Compute XxHash128
                var computedHash = await ComputeXxHash128Async(fileStream);

                // 3. Compare hashes
                if (!string.Equals(computedHash, evt.XxHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Hash mismatch for job {evt.JobId}: expected {evt.XxHash}, got {computedHash}");

                _logger.LogInformation("Job {JobId}: Hash verified successfully", evt.JobId);

                // 4. Create scope and get DbContext
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                // 5. Insert movie entity (verified=false)
                var now = DateTime.UtcNow;
                movieId = Guid.NewGuid();
                var movie = new Movie
                {
                    Id = movieId.Value,
                    Title = evt.Title,
                    Description = evt.Description,
                    ReleaseYear = evt.ReleaseYear,
                    DurationMinutes = evt.DurationMinutes,
                    FilePath = evt.StagedPath,
                    XxHash = evt.XxHash,
                    FileSizeBytes = evt.FileSizeBytes,
                    Verified = false,
                    StorageConnectionString = evt.StorageConnectionString,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                db.Movies.Add(movie);

                // 6. SaveChanges (metadata committed but untrusted)
                await db.SaveChangesAsync();
                _logger.LogInformation("Job {JobId}: Movie record inserted (verified=false), MovieId: {MovieId}",
                    evt.JobId, movieId);

                // 7. Copy file from .part to final name via FluentStorage
                var finalFileName = evt.StagedPath.Replace(".part", ".ready");

                await using (var readStream = await storage.OpenReadAsync(evt.StagedPath))
                {
                    if (readStream is not null)
                        await storage.WriteAsync(finalFileName, readStream, false);
                }

                // 8. Delete .part file
                await storage.DeleteAsync(new[] { evt.StagedPath });

                _logger.LogInformation("Job {JobId}: File promoted from {Part} to {Final}",
                    evt.JobId, evt.StagedPath, finalFileName);

                // 9. Update movie: set verified=true and update file path
                movie.FilePath = finalFileName;
                movie.Verified = true;
                movie.UpdatedAt = DateTime.UtcNow;

                // 10. SaveChanges
                await db.SaveChangesAsync();

                _logger.LogInformation("Job {JobId}: Movie verified and committed. MovieId: {MovieId}",
                    evt.JobId, movieId);

                return; // Success - exit retry loop
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobId}: Attempt {Attempt}/{Max} failed: {Message}",
                    evt.JobId, attempt, MaxRetryAttempts, ex.Message);

                if (attempt == MaxRetryAttempts)
                {
                    // Final attempt failed - cleanup
                    _logger.LogError("Job {JobId}: All {Max} attempts exhausted. Cleaning up.",
                        evt.JobId, MaxRetryAttempts);

                    await CleanupFailedIngestionAsync(evt, movieId);
                }
                else
                {
                    // Exponential backoff: 1s, 2s
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    _logger.LogInformation("Job {JobId}: Retrying in {Delay}...", evt.JobId, delay);
                    await Task.Delay(delay);
                }
            }
        }
    }

    private async Task CleanupFailedIngestionAsync(FileIngestedEvent evt, Guid? movieId)
    {
        try
        {
            // Clean up DB record if it was created
            if (movieId.HasValue)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();
                var movie = await db.Movies.FindAsync(movieId.Value);
                if (movie is not null)
                {
                    db.Movies.Remove(movie);
                    await db.SaveChangesAsync();
                    _logger.LogInformation("Job {JobId}: Cleaned up movie record {MovieId}", evt.JobId, movieId);
                }
            }

            // Clean up .part file if it exists
            try
            {
                using var storage = StorageFactory.Blobs.FromConnectionString(evt.StorageConnectionString);
                await storage.DeleteAsync(new[] { evt.StagedPath });
                _logger.LogInformation("Job {JobId}: Cleaned up staged file {Path}", evt.JobId, evt.StagedPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job {JobId}: Failed to clean up staged file", evt.JobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId}: Cleanup failed", evt.JobId);
        }
    }

    private async Task HandleMovieQueryAsync(IMessageContext<MovieQueryRequest> context)
    {
        var request = context.Message;
        _logger.LogInformation("Received movie query: TitleSearch={Title}, Year={Year}, IncludeUnverified={Unverified}",
            request.TitleSearch, request.ReleaseYear, request.IncludeUnverified);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

            var query = db.Movies.AsNoTracking().AsQueryable();

            if (!request.IncludeUnverified)
                query = query.Where(m => m.Verified);

            if (!string.IsNullOrWhiteSpace(request.TitleSearch))
                query = query.Where(m => EF.Functions.ILike(m.Title, $"%{request.TitleSearch}%"));

            if (request.ReleaseYear.HasValue)
                query = query.Where(m => m.ReleaseYear == request.ReleaseYear.Value);

            var totalCount = await query.CountAsync();

            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 100);

            var movies = await query
                .OrderByDescending(m => m.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(m => new MovieDto
                {
                    Id = m.Id,
                    Title = m.Title,
                    Description = m.Description,
                    ReleaseYear = m.ReleaseYear,
                    DurationMinutes = m.DurationMinutes,
                    FilePath = m.FilePath,
                    XxHash = m.XxHash,
                    FileSizeBytes = m.FileSizeBytes,
                    Verified = m.Verified,
                    CreatedAt = m.CreatedAt
                })
                .ToListAsync();

            await context.RespondAsync(new MovieQueryResponse
            {
                Movies = movies,
                TotalCount = totalCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle movie query");
            await context.RespondAsync(new MovieQueryResponse
            {
                Movies = new List<MovieDto>(),
                TotalCount = 0
            });
        }
    }

    private async Task HandleMovieGetAsync(IMessageContext<MovieGetRequest> context)
    {
        var request = context.Message;
        _logger.LogInformation("Received movie get request for {MovieId}", request.MovieId);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

            var movie = await db.Movies.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.MovieId);

            if (movie is null)
            {
                await context.RespondAsync(new MovieGetResponse { Movie = null });
                return;
            }

            await context.RespondAsync(new MovieGetResponse
            {
                Movie = new MovieDto
                {
                    Id = movie.Id,
                    Title = movie.Title,
                    Description = movie.Description,
                    ReleaseYear = movie.ReleaseYear,
                    DurationMinutes = movie.DurationMinutes,
                    FilePath = movie.FilePath,
                    XxHash = movie.XxHash,
                    FileSizeBytes = movie.FileSizeBytes,
                    Verified = movie.Verified,
                    CreatedAt = movie.CreatedAt
                },
                StorageConnectionString = movie.StorageConnectionString,
                StoragePath = movie.FilePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle movie get request for {MovieId}", request.MovieId);
            await context.RespondAsync(new MovieGetResponse { Movie = null });
        }
    }

    private static async Task<string> ComputeXxHash128Async(Stream stream)
    {
        var hash = new XxHash128();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            hash.Append(buffer.AsSpan(0, bytesRead));
        }
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }
}
