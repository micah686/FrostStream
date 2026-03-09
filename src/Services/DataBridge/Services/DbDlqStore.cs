using System.Text.Json;
using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Entities;

namespace DataBridge.Services;

/// <summary>
/// PostgreSQL/EF Core backed implementation of <see cref="IDlqStore"/>.
/// Stores DLQ entries in the database for persistence, querying, and management.
/// </summary>
public class DbDlqStore : IDlqStore
{
    private readonly FrostStreamDbContext _dbContext;
    private readonly ILogger<DbDlqStore> _logger;

    public DbDlqStore(
        FrostStreamDbContext dbContext,
        ILogger<DbDlqStore> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StoreAsync(DlqMessageEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);

        try
        {
            // Check if entry already exists (idempotency)
            var existingEntry = await _dbContext.DlqEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EntryKey == entry.Id, cancellationToken);

            if (existingEntry != null)
            {
                _logger.LogDebug("DLQ entry {EntryKey} already exists, skipping duplicate store", entry.Id);
                return;
            }

            var dlqEntry = new DlqEntry
            {
                Id = Guid.NewGuid(),
                EntryKey = entry.Id,
                OriginalStream = entry.OriginalStream,
                OriginalConsumer = entry.OriginalConsumer,
                OriginalSubject = entry.OriginalSubject,
                OriginalSequence = entry.OriginalSequence,
                DeliveryCount = entry.DeliveryCount,
                FailedAt = entry.StoredAt, // Use StoredAt as FailedAt since advisory doesn't have separate FailedAt
                StoredAt = DateTimeOffset.UtcNow,
                ErrorReason = entry.ErrorReason,
                Status = MapStatus(entry.Status)
            };

            _dbContext.DlqEntries.Add(dlqEntry);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Stored DLQ entry {EntryId} for {Stream}/{Consumer} (Seq: {Sequence})",
                dlqEntry.Id, entry.OriginalStream, entry.OriginalConsumer, entry.OriginalSequence);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another instance stored it concurrently - this is fine
            _logger.LogDebug(ex, "DLQ entry {EntryKey} was stored by another instance, ignoring", entry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store DLQ entry {EntryKey}", entry.Id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DlqMessageEntry?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            var entry = await _dbContext.DlqEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EntryKey == id, cancellationToken);

            if (entry == null)
                return null;

            return MapToDlqMessageEntry(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve DLQ entry {EntryKey}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DlqMessageEntry>> ListAsync(
        string? filterStream = null,
        string? filterConsumer = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _dbContext.DlqEntries
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(filterStream))
            {
                query = query.Where(e => e.OriginalStream == filterStream);
            }

            if (!string.IsNullOrWhiteSpace(filterConsumer))
            {
                query = query.Where(e => e.OriginalConsumer == filterConsumer);
            }

            var entries = await query
                .OrderByDescending(e => e.StoredAt)
                .Take(limit)
                .ToListAsync(cancellationToken);

            return entries.Select(MapToDlqMessageEntry).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list DLQ entries with filters Stream={Stream}, Consumer={Consumer}",
                filterStream, filterConsumer);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateStatusAsync(string id, DlqMessageStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            var entry = await _dbContext.DlqEntries
                .FirstOrDefaultAsync(e => e.EntryKey == id, cancellationToken);

            if (entry == null)
            {
                _logger.LogWarning("Cannot update status for DLQ entry {EntryKey}: not found", id);
                return false;
            }

            entry.Status = MapStatus(status);
            entry.StatusUpdatedAt = DateTimeOffset.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Updated DLQ entry {EntryKey} status to {Status}", id, status);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update DLQ entry {EntryKey} status", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            var entry = await _dbContext.DlqEntries
                .FirstOrDefaultAsync(e => e.EntryKey == id, cancellationToken);

            if (entry == null)
            {
                _logger.LogWarning("Cannot delete DLQ entry {EntryKey}: not found", id);
                return false;
            }

            _dbContext.DlqEntries.Remove(entry);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Deleted DLQ entry {EntryKey}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete DLQ entry {EntryKey}", id);
            throw;
        }
    }

    private static DlqMessageEntry MapToDlqMessageEntry(DlqEntry entry)
    {
        return new DlqMessageEntry
        {
            Id = entry.EntryKey,
            OriginalStream = entry.OriginalStream,
            OriginalConsumer = entry.OriginalConsumer,
            OriginalSubject = entry.OriginalSubject,
            OriginalSequence = entry.OriginalSequence,
            DeliveryCount = entry.DeliveryCount,
            StoredAt = entry.StoredAt,
            ErrorReason = entry.ErrorReason ?? "Unknown error",
            Status = MapStatus(entry.Status)
        };
    }

    private static DlqEntryStatus MapStatus(DlqMessageStatus status)
    {
        return status switch
        {
            DlqMessageStatus.Pending => DlqEntryStatus.Pending,
            DlqMessageStatus.Processing => DlqEntryStatus.Reviewed,  // Processing = under review
            DlqMessageStatus.Resolved => DlqEntryStatus.Retried,     // Resolved = successfully retried
            DlqMessageStatus.Archived => DlqEntryStatus.Discarded,   // Archived = discarded
            _ => DlqEntryStatus.Pending
        };
    }

    private static DlqMessageStatus MapStatus(DlqEntryStatus status)
    {
        return status switch
        {
            DlqEntryStatus.Pending => DlqMessageStatus.Pending,
            DlqEntryStatus.Reviewed => DlqMessageStatus.Processing,
            DlqEntryStatus.Retried => DlqMessageStatus.Resolved,
            DlqEntryStatus.Discarded => DlqMessageStatus.Archived,
            _ => DlqMessageStatus.Pending
        };
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // PostgreSQL unique violation SQL state is 23505
        return ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505";
    }
}
