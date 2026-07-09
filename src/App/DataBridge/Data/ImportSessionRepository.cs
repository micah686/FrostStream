using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shared.Database;
using Shared.Imports;
using Shared.Messaging;
using System.Text.Json;

namespace DataBridge.Data;

public sealed class ImportSessionRepository(DataBridgeDbContext db, IClock clock) : IImportSessionRepository
{
    private const int MaxListLimit = 100;
    private const int MaxItemsLimit = 200;
    private const int InsertBatchSize = 500;

    public async Task<ImportSessionDto> CreateAsync(
        ImportSessionCreateRequest request,
        Guid sessionId,
        Guid correlationId,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var entity = new ImportSessionEntity
        {
            SessionId = sessionId,
            CorrelationId = correlationId,
            Status = ImportSessionStatus.Scanning,
            SourceKind = request.SourceKind,
            SourceRoot = request.SourceKind == ImportSessionSourceKind.WorkerIncoming
                ? LocalImportIncoming.SourceRootMarker
                : "storage",
            SubPath = NormalizeOptional(request.SubPath),
            StorageKey = NormalizeRequired(request.StorageKey, "storageKey"),
            WorkerTag = NormalizeOptional(request.WorkerTag),
            RequestedBy = NormalizeOptional(request.RequestedBy),
            MaxParallelItems = request.MaxParallelItems is >= 1 and <= 64 ? request.MaxParallelItems.Value : 6,
            UpdatedAt = now
        };

        db.ImportSessions.Add(entity);
        await db.SaveChangesAsync(ct);

        return ToDto(entity);
    }

    public async Task<IReadOnlyList<ImportSessionDto>> ListAsync(ImportSessionListRequest request, CancellationToken ct = default)
    {
        var limit = Math.Clamp(request.Limit, 1, MaxListLimit);
        var query = db.ImportSessions.AsNoTracking();

        if (request.Status is { } status)
            query = query.Where(x => x.Status == status);
        if (request.AfterSessionId is { } after)
            query = query.Where(x => x.SessionId.CompareTo(after) > 0);

        return await query
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.SessionId)
            .Take(limit + 1)
            .Select(x => ToDto(x))
            .ToListAsync(ct);
    }

    public async Task<ImportSessionDto?> GetAsync(Guid sessionId, CancellationToken ct = default)
        => await db.ImportSessions
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId)
            .Select(x => ToDto(x))
            .FirstOrDefaultAsync(ct);

    public async Task<(IReadOnlyList<ImportSessionItemDto> Items, Guid? NextItemId, int TotalCount)> ListItemsAsync(
        ImportSessionItemsListRequest request,
        CancellationToken ct = default)
    {
        var limit = Math.Clamp(request.Limit, 1, MaxItemsLimit);
        var query = db.ImportSessionItems
            .AsNoTracking()
            .Where(x => x.SessionId == request.SessionId);

        if (request.Status is { } status)
            query = query.Where(x => x.Status == status);
        if (request.MetadataState is { } metadataState)
            query = query.Where(x => x.MetadataState == metadataState);
        if (request.AfterItemId is { } after)
            query = query.Where(x => x.ItemId.CompareTo(after) > 0);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var pattern = $"%{request.Search.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.RelativePath, pattern)
                || EF.Functions.ILike(x.FileName, pattern)
                || (x.Title != null && EF.Functions.ILike(x.Title, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(x => x.ItemId)
            .Take(limit + 1)
            .Select(x => ToItemDto(x))
            .ToListAsync(ct);

        Guid? next = null;
        if (rows.Count > limit)
        {
            next = rows[limit - 1].ItemId;
            rows.RemoveAt(rows.Count - 1);
        }

        return (rows, next, total);
    }

    public async Task<ImportSessionDto?> IngestScannedItemsAsync(
        Guid sessionId,
        IReadOnlyList<ImportSessionScannedItem> items,
        CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return null;

        var now = clock.GetCurrentInstant();
        var existing = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId)
            .Select(x => x.RelativePath)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var pending = new List<ImportSessionItemEntity>(InsertBatchSize);
        foreach (var item in items)
        {
            if (!existing.Add(item.RelativePath))
                continue;

            pending.Add(new ImportSessionItemEntity
            {
                ItemId = Guid.NewGuid(),
                SessionId = sessionId,
                RelativePath = item.RelativePath,
                FileName = item.FileName,
                FileSizeBytes = item.FileSizeBytes,
                FileMtime = item.FileMtime,
                SidecarsJson = NormalizeJson(item.SidecarsJson),
                Provider = NormalizeOptional(item.Provider),
                SourceMediaId = NormalizeOptional(item.SourceMediaId),
                SourceUrl = NormalizeOptional(item.SourceUrl),
                Title = NormalizeOptional(item.Title),
                ScanMetadataJson = NormalizeJson(item.ScanMetadataJson),
                MetadataState = item.MetadataState,
                Status = ImportSessionItemStatus.Discovered,
                UpdatedAt = now
            });

            if (pending.Count >= InsertBatchSize)
                await FlushAsync(pending, ct);
        }

        if (pending.Count > 0)
            await FlushAsync(pending, ct);

        var counts = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId)
            .GroupBy(x => x.SessionId)
            .Select(g => new
            {
                Total = g.Count(),
                Ready = g.Count(x => x.MetadataState == ImportSessionItemMetadataState.Ready || x.MetadataState == ImportSessionItemMetadataState.Edited || x.MetadataState == ImportSessionItemMetadataState.PlaceholderAccepted),
                Incomplete = g.Count(x => x.MetadataState == ImportSessionItemMetadataState.Incomplete),
                Excluded = g.Count(x => x.Excluded)
            })
            .FirstOrDefaultAsync(ct);

        session.Status = ImportSessionStatus.Reviewing;
        session.TotalItems = counts?.Total ?? 0;
        session.ReadyItems = counts?.Ready ?? 0;
        session.IncompleteItems = counts?.Incomplete ?? 0;
        session.ExcludedItems = counts?.Excluded ?? 0;
        session.ErrorMessage = null;
        session.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);

        return ToDto(session);

        async Task FlushAsync(List<ImportSessionItemEntity> batch, CancellationToken cancellationToken)
        {
            db.ImportSessionItems.AddRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            batch.Clear();
            session = await db.ImportSessions.FirstAsync(x => x.SessionId == sessionId, cancellationToken);
        }
    }

    public async Task<ImportSessionDto?> MarkScanFailedAsync(Guid sessionId, string errorMessage, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return null;

        session.Status = ImportSessionStatus.ScanFailed;
        session.ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "Scan failed." : errorMessage;
        session.UpdatedAt = clock.GetCurrentInstant();
        session.CompletedAt = session.UpdatedAt;
        await db.SaveChangesAsync(ct);
        return ToDto(session);
    }

    public async Task<IReadOnlyList<ImportSessionProbeItemRef>> ListItemsForProbeAsync(
        Guid sessionId,
        int batchSize,
        CancellationToken ct = default)
        => await db.ImportSessionItems
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId && x.Status == ImportSessionItemStatus.Discovered && !x.Excluded)
            .OrderBy(x => x.ItemId)
            .Take(Math.Clamp(batchSize, 1, 25))
            .Select(x => new ImportSessionProbeItemRef { ItemId = x.ItemId, RelativePath = x.RelativePath })
            .ToListAsync(ct);

    public async Task<ImportSessionDto?> ApplyProbeResultsAsync(
        Guid sessionId,
        IReadOnlyList<ImportSessionProbeResult> results,
        CancellationToken ct = default)
    {
        if (results.Count == 0)
            return await GetAsync(sessionId, ct);

        var byId = results.ToDictionary(x => x.ItemId);
        var ids = byId.Keys.ToList();
        var rows = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId && ids.Contains(x.ItemId))
            .ToListAsync(ct);
        var now = clock.GetCurrentInstant();

        foreach (var item in rows)
        {
            var result = byId[item.ItemId];
            item.ProbeMetadataJson = NormalizeJson(result.ProbeMetadataJson);
            item.Status = ImportSessionItemStatus.Probed;
            item.ErrorCode = null;
            item.ErrorMessage = null;
            item.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return await RecalculateCountersAsync(sessionId, ct);
    }

    public async Task<ImportSessionDto?> ApplyProbeFailuresAsync(
        Guid sessionId,
        IReadOnlyList<ImportSessionProbeFailure> failures,
        CancellationToken ct = default)
    {
        if (failures.Count == 0)
            return await GetAsync(sessionId, ct);

        var byId = failures.ToDictionary(x => x.ItemId);
        var ids = byId.Keys.ToList();
        var rows = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId && ids.Contains(x.ItemId))
            .ToListAsync(ct);
        var now = clock.GetCurrentInstant();

        foreach (var item in rows)
        {
            var failure = byId[item.ItemId];
            item.Status = ImportSessionItemStatus.Failed;
            item.ErrorCode = NormalizeOptional(failure.ErrorCode) ?? "probe_failed";
            item.ErrorMessage = NormalizeOptional(failure.ErrorMessage) ?? "ffprobe failed.";
            item.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return await RecalculateCountersAsync(sessionId, ct);
    }

    public async Task<(ImportSessionItemDto? Item, ImportSessionDto? Session)> PatchItemAsync(
        ImportSessionItemPatchRequest request,
        CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems
            .FirstOrDefaultAsync(x => x.SessionId == request.SessionId && x.ItemId == request.ItemId, ct);
        if (item is null)
            return (null, await GetAsync(request.SessionId, ct));

        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["title"] = NormalizeOptional(request.Title),
            ["provider"] = NormalizeOptional(request.Provider),
            ["sourceMediaId"] = NormalizeOptional(request.SourceMediaId),
            ["sourceUrl"] = NormalizeOptional(request.SourceUrl)
        };
        item.Title = metadata["title"] ?? item.Title;
        item.Provider = metadata["provider"] ?? item.Provider;
        item.SourceMediaId = metadata["sourceMediaId"] ?? item.SourceMediaId;
        item.SourceUrl = metadata["sourceUrl"] ?? item.SourceUrl;
        item.UserMetadataJson = JsonSerializer.Serialize(metadata.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value));
        item.MetadataState = ImportSessionItemMetadataState.Edited;
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);

        var session = await RecalculateCountersAsync(request.SessionId, ct);
        await db.Entry(item).ReloadAsync(ct);
        return (ToItemDto(item), session);
    }

    public async Task<(int AffectedCount, ImportSessionDto? Session)> ApplyBulkAsync(
        ImportSessionItemsBulkRequest request,
        CancellationToken ct = default)
    {
        var query = db.ImportSessionItems.Where(x => x.SessionId == request.SessionId);
        if (request.ItemIds is { Count: > 0 })
        {
            var ids = request.ItemIds.ToHashSet();
            query = query.Where(x => ids.Contains(x.ItemId));
        }
        else
        {
            if (request.Status is { } status)
                query = query.Where(x => x.Status == status);
            if (request.MetadataState is { } metadataState)
                query = query.Where(x => x.MetadataState == metadataState);
            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var pattern = $"%{request.Search.Trim()}%";
                query = query.Where(x =>
                    EF.Functions.ILike(x.RelativePath, pattern)
                    || EF.Functions.ILike(x.FileName, pattern)
                    || (x.Title != null && EF.Functions.ILike(x.Title, pattern)));
            }
        }

        var rows = await query.ToListAsync(ct);
        var now = clock.GetCurrentInstant();
        foreach (var item in rows)
        {
            switch (request.Action)
            {
                case ImportSessionBulkAction.AcceptPlaceholders:
                    if (item.MetadataState == ImportSessionItemMetadataState.Incomplete)
                        item.MetadataState = ImportSessionItemMetadataState.PlaceholderAccepted;
                    break;
                case ImportSessionBulkAction.Exclude:
                    item.Excluded = true;
                    break;
                case ImportSessionBulkAction.Include:
                    item.Excluded = false;
                    break;
                case ImportSessionBulkAction.ResetFailed:
                    if (item.Status == ImportSessionItemStatus.Failed)
                    {
                        item.Status = ImportSessionItemStatus.Discovered;
                        item.ErrorCode = null;
                        item.ErrorMessage = null;
                    }
                    break;
            }

            item.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        var session = await RecalculateCountersAsync(request.SessionId, ct);
        return (rows.Count, session);
    }

    public async Task<(int MatchedCount, int UnmatchedCount, ImportSessionDto? Session)> ApplyMappingAsync(
        Guid sessionId,
        IReadOnlyList<ImportSessionMappingRow> rows,
        string objectBucket,
        string objectKey,
        string format,
        CancellationToken ct = default)
    {
        var sessionExists = await db.ImportSessions.AnyAsync(x => x.SessionId == sessionId, ct);
        if (!sessionExists)
            return (0, rows.Count, null);

        var items = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(ct);
        var byFileName = items
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var byRelativePath = items.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);

        var matched = 0;
        var unmatched = 0;
        var now = clock.GetCurrentInstant();
        foreach (var row in rows)
        {
            if (!byRelativePath.TryGetValue(row.FileName, out var item)
                && !byFileName.TryGetValue(row.FileName, out item))
            {
                unmatched++;
                continue;
            }

            item.Title = NormalizeOptional(row.Title) ?? item.Title;
            item.Provider = NormalizeOptional(row.Provider) ?? item.Provider;
            item.SourceMediaId = NormalizeOptional(row.SourceMediaId) ?? item.SourceMediaId;
            item.SourceUrl = NormalizeOptional(row.SourceUrl) ?? item.SourceUrl;
            item.UserMetadataJson = JsonSerializer.Serialize(new
            {
                title = NormalizeOptional(row.Title),
                provider = NormalizeOptional(row.Provider),
                sourceMediaId = NormalizeOptional(row.SourceMediaId),
                sourceUrl = NormalizeOptional(row.SourceUrl)
            });
            item.MetadataState = ImportSessionItemMetadataState.Edited;
            item.UpdatedAt = now;
            matched++;
        }

        db.ImportSessionMappings.Add(new ImportSessionMappingEntity
        {
            MappingId = Guid.NewGuid(),
            SessionId = sessionId,
            ObjectBucket = objectBucket,
            ObjectKey = objectKey,
            Format = format,
            MatchedCount = matched,
            UnmatchedCount = unmatched
        });

        await db.SaveChangesAsync(ct);
        var session = await RecalculateCountersAsync(sessionId, ct);
        return (matched, unmatched, session);
    }

    public async Task<IReadOnlyList<ImportSessionEnrichItemRef>> ListItemsForEnrichAsync(
        Guid sessionId,
        IReadOnlyList<Guid>? itemIds,
        int limit,
        CancellationToken ct = default)
    {
        var query = db.ImportSessionItems
            .AsNoTracking()
            .Where(x => x.SessionId == sessionId
                        && !x.Excluded
                        && x.SourceUrl != null
                        && x.EnrichedMetadataJson == null
                        && (x.Status == ImportSessionItemStatus.Discovered || x.Status == ImportSessionItemStatus.Probed));

        if (itemIds is { Count: > 0 })
        {
            var ids = itemIds.ToHashSet();
            query = query.Where(x => ids.Contains(x.ItemId));
        }

        return await query
            .OrderBy(x => x.ItemId)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(x => new ImportSessionEnrichItemRef
            {
                ItemId = x.ItemId,
                SourceUrl = x.SourceUrl!,
                Provider = x.Provider
            })
            .ToListAsync(ct);
    }

    public async Task<ImportSessionDto?> ApplyEnrichmentAsync(ImportSessionItemEnriched message, CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems
            .FirstOrDefaultAsync(x => x.SessionId == message.SessionId && x.ItemId == message.ItemId, ct);
        if (item is null)
            return await GetAsync(message.SessionId, ct);

        item.EnrichedMetadataJson = NormalizeJson(message.EnrichedMetadataJson);

        // Enrichment sits below user edits in the layered merge: only refresh the effective
        // display columns while the item has not been touched by the user.
        if (item.MetadataState is ImportSessionItemMetadataState.Incomplete or ImportSessionItemMetadataState.Ready)
        {
            item.Title = NormalizeOptional(message.Title) ?? item.Title;
            item.Provider = NormalizeOptional(message.Provider) ?? item.Provider;
            item.SourceMediaId = NormalizeOptional(message.SourceMediaId) ?? item.SourceMediaId;
            item.SourceUrl = NormalizeOptional(message.SourceUrl) ?? item.SourceUrl;
            if (item.MetadataState == ImportSessionItemMetadataState.Incomplete && !string.IsNullOrWhiteSpace(item.Title))
                item.MetadataState = ImportSessionItemMetadataState.Ready;
        }

        if (item.ErrorCode?.StartsWith("enrich", StringComparison.Ordinal) == true)
        {
            item.ErrorCode = null;
            item.ErrorMessage = null;
        }

        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return await RecalculateCountersAsync(message.SessionId, ct);
    }

    public async Task<ImportSessionDto?> ApplyEnrichFailureAsync(ImportSessionItemEnrichFailed message, CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems
            .FirstOrDefaultAsync(x => x.SessionId == message.SessionId && x.ItemId == message.ItemId, ct);
        if (item is null)
            return await GetAsync(message.SessionId, ct);

        // Enrichment is optional; surface the error for the review UI without failing the item.
        item.ErrorCode = NormalizeOptional(message.ErrorCode) ?? "enrich_failed";
        item.ErrorMessage = NormalizeOptional(message.ErrorMessage) ?? "yt-dlp enrichment failed.";
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return await GetAsync(message.SessionId, ct);
    }

    public async Task<(ImportSessionDto? Session, int ApprovedCount, string? Error)> CommitAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return (null, 0, null);
        if (session.Status is ImportSessionStatus.Cancelled or ImportSessionStatus.Completed)
            return (ToDto(session), 0, "Session is terminal.");

        var blockerCount = await db.ImportSessionItems.CountAsync(
            x => x.SessionId == sessionId && !x.Excluded && x.MetadataState == ImportSessionItemMetadataState.Incomplete,
            ct);
        if (blockerCount > 0)
            return (ToDto(session), 0, $"{blockerCount} incomplete item(s) must be edited, excluded, or placeholder-accepted before commit.");

        var eligible = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId
                        && !x.Excluded
                        && (x.Status == ImportSessionItemStatus.Discovered || x.Status == ImportSessionItemStatus.Probed)
                        && x.MetadataState != ImportSessionItemMetadataState.Incomplete)
            .ToListAsync(ct);

        var now = clock.GetCurrentInstant();
        foreach (var item in eligible)
        {
            item.Status = ImportSessionItemStatus.Approved;
            item.ErrorCode = null;
            item.ErrorMessage = null;
            item.UpdatedAt = now;
        }

        session.Status = ImportSessionStatus.Committing;
        session.ErrorMessage = null;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        var updated = await RecalculateCountersAsync(sessionId, ct);
        return (updated, eligible.Count, null);
    }

    public async Task<(ImportSessionDto? Session, int ResetCount)> RetryFailedAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return (null, 0);

        var rows = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId
                        && !x.Excluded
                        && x.Status == ImportSessionItemStatus.Failed
                        && x.MetadataState != ImportSessionItemMetadataState.Incomplete)
            .ToListAsync(ct);

        var now = clock.GetCurrentInstant();
        foreach (var item in rows)
        {
            item.Status = ImportSessionItemStatus.Approved;
            item.Attempt += 1;
            item.ErrorCode = null;
            item.ErrorMessage = null;
            item.UpdatedAt = now;
            item.CompletedAt = null;
        }

        session.Status = ImportSessionStatus.Committing;
        session.CompletedAt = null;
        session.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (await RecalculateCountersAsync(sessionId, ct), rows.Count);
    }

    public async Task<ImportSessionDto?> CancelAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return null;
        if (session.Status is ImportSessionStatus.Completed or ImportSessionStatus.CompletedWithFailures or ImportSessionStatus.Cancelled)
            return ToDto(session);

        var now = clock.GetCurrentInstant();
        session.Status = ImportSessionStatus.Cancelled;
        session.UpdatedAt = now;
        session.CompletedAt = now;
        await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId
                        && (x.Status == ImportSessionItemStatus.Discovered
                            || x.Status == ImportSessionItemStatus.Probed
                            || x.Status == ImportSessionItemStatus.Approved))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Excluded, true)
                .SetProperty(x => x.UpdatedAt, now), ct);
        await db.SaveChangesAsync(ct);
        return await RecalculateCountersAsync(sessionId, ct);
    }

    public async Task<IReadOnlyList<ImportSessionDto>> ListCommittingSessionsAsync(int limit, CancellationToken ct = default)
        => await db.ImportSessions
            .AsNoTracking()
            .Where(x => x.Status == ImportSessionStatus.Committing)
            .OrderBy(x => x.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => ToDto(x))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ImportSessionItemWork>> ListApprovedWorkAsync(Guid sessionId, int limit, CancellationToken ct = default)
        => await BuildWorkQuery(sessionId)
            .Where(x => x.Item.Status == ImportSessionItemStatus.Approved)
            .OrderBy(x => x.Item.ItemId)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => ToWork(x.Session, x.Item))
            .ToListAsync(ct);

    public async Task<ImportSessionItemWork?> GetItemWorkAsync(Guid sessionId, Guid itemId, CancellationToken ct = default)
        => await BuildWorkQuery(sessionId)
            .Where(x => x.Item.ItemId == itemId)
            .Select(x => ToWork(x.Session, x.Item))
            .FirstOrDefaultAsync(ct);

    public Task MarkItemHashingAsync(Guid sessionId, Guid itemId, CancellationToken ct = default)
        => UpdateItemStatusAsync(sessionId, itemId, ImportSessionItemStatus.Hashing, ct);

    public async Task MarkItemPreparedAsync(Guid sessionId, Guid itemId, LocalImportFilePrepared prepared, CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems.FirstAsync(x => x.SessionId == sessionId && x.ItemId == itemId, ct);
        item.ContentHashXxh128 = NormalizeOptional(prepared.ContentHashXxh128)?.ToLowerInvariant();
        item.FileSizeBytes = prepared.FileSizeBytes;
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkItemUploadingAsync(Guid sessionId, Guid itemId, Guid mediaGuid, string storagePath, CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems.FirstAsync(x => x.SessionId == sessionId && x.ItemId == itemId, ct);
        item.Status = ImportSessionItemStatus.Uploading;
        item.MediaGuid = mediaGuid;
        item.StoragePath = storagePath;
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        await RecalculateCountersAsync(sessionId, ct);
    }

    public Task MarkItemFinalizingAsync(Guid sessionId, Guid itemId, CancellationToken ct = default)
        => UpdateItemStatusAsync(sessionId, itemId, ImportSessionItemStatus.Finalizing, ct);

    public async Task MarkItemAlreadyImportedAsync(Guid sessionId, Guid itemId, Guid mediaGuid, string storagePath, LocalImportFilePrepared prepared, CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems.FirstAsync(x => x.SessionId == sessionId && x.ItemId == itemId, ct);
        item.Status = ImportSessionItemStatus.AlreadyImported;
        item.MediaGuid = mediaGuid;
        item.StoragePath = storagePath;
        item.FileSizeBytes = prepared.FileSizeBytes;
        item.ContentHashXxh128 = NormalizeOptional(prepared.ContentHashXxh128)?.ToLowerInvariant();
        item.UpdatedAt = clock.GetCurrentInstant();
        item.CompletedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await RecalculateCountersAsync(sessionId, ct);
    }

    public async Task MarkItemImportedAsync(
        Guid sessionId,
        Guid itemId,
        Guid mediaGuid,
        string storagePath,
        string? storageVersion,
        string? metaStoragePath,
        string? infoJsonStoragePath,
        string? thumbnailStoragePath,
        string? captionStoragePathsJson,
        CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems.FirstAsync(x => x.SessionId == sessionId && x.ItemId == itemId, ct);
        item.Status = ImportSessionItemStatus.Imported;
        item.MediaGuid = mediaGuid;
        item.StoragePath = storagePath;
        item.StorageVersion = storageVersion;
        item.MetaStoragePath = metaStoragePath;
        item.InfoJsonStoragePath = infoJsonStoragePath;
        item.ThumbnailStoragePath = thumbnailStoragePath;
        item.CaptionStoragePathsJson = captionStoragePathsJson;
        item.UpdatedAt = clock.GetCurrentInstant();
        item.CompletedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await RecalculateCountersAsync(sessionId, ct);
    }

    public async Task MarkItemCommitFailedAsync(
        Guid sessionId,
        Guid itemId,
        string? errorCode,
        string errorMessage,
        Guid? mediaGuid = null,
        string? storagePath = null,
        CancellationToken ct = default)
    {
        var item = await db.ImportSessionItems.FirstAsync(x => x.SessionId == sessionId && x.ItemId == itemId, ct);
        item.Status = ImportSessionItemStatus.Failed;
        item.ErrorCode = NormalizeOptional(errorCode) ?? "item_failed";
        item.ErrorMessage = NormalizeOptional(errorMessage) ?? "Import item failed.";
        item.MediaGuid = mediaGuid ?? item.MediaGuid;
        item.StoragePath = storagePath ?? item.StoragePath;
        item.UpdatedAt = clock.GetCurrentInstant();
        item.CompletedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await RecalculateCountersAsync(sessionId, ct);
    }

    public async Task<ImportSessionDto?> CompleteSessionIfTerminalAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return null;
        if (session.Status != ImportSessionStatus.Committing)
            return await RecalculateCountersAsync(sessionId, ct);

        var remaining = await db.ImportSessionItems.CountAsync(
            x => x.SessionId == sessionId
                 && !x.Excluded
                 && (x.Status == ImportSessionItemStatus.Approved
                     || x.Status == ImportSessionItemStatus.Hashing
                     || x.Status == ImportSessionItemStatus.Uploading
                     || x.Status == ImportSessionItemStatus.Finalizing),
            ct);
        if (remaining > 0)
            return await RecalculateCountersAsync(sessionId, ct);

        var failed = await db.ImportSessionItems.AnyAsync(x => x.SessionId == sessionId && x.Status == ImportSessionItemStatus.Failed, ct);
        session.Status = failed ? ImportSessionStatus.CompletedWithFailures : ImportSessionStatus.Completed;
        session.UpdatedAt = clock.GetCurrentInstant();
        session.CompletedAt = session.UpdatedAt;
        await db.SaveChangesAsync(ct);
        return await RecalculateCountersAsync(sessionId, ct);
    }

    private async Task UpdateItemStatusAsync(Guid sessionId, Guid itemId, ImportSessionItemStatus status, CancellationToken ct)
    {
        var item = await db.ImportSessionItems.FirstAsync(x => x.SessionId == sessionId && x.ItemId == itemId, ct);
        item.Status = status;
        item.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        await RecalculateCountersAsync(sessionId, ct);
    }

    private IQueryable<ImportSessionWorkProjection> BuildWorkQuery(Guid sessionId)
        => db.ImportSessions
            .Where(session => session.SessionId == sessionId)
            .Join(
                db.ImportSessionItems,
                session => session.SessionId,
                item => item.SessionId,
                (session, item) => new ImportSessionWorkProjection(session, item));

    private async Task<ImportSessionDto?> RecalculateCountersAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.ImportSessions.FirstOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (session is null)
            return null;

        var counts = await db.ImportSessionItems
            .Where(x => x.SessionId == sessionId)
            .GroupBy(x => x.SessionId)
            .Select(g => new
            {
                Total = g.Count(),
                Probed = g.Count(x => x.Status == ImportSessionItemStatus.Probed),
                Ready = g.Count(x => x.MetadataState == ImportSessionItemMetadataState.Ready || x.MetadataState == ImportSessionItemMetadataState.Edited || x.MetadataState == ImportSessionItemMetadataState.PlaceholderAccepted),
                Incomplete = g.Count(x => x.MetadataState == ImportSessionItemMetadataState.Incomplete),
                Excluded = g.Count(x => x.Excluded),
                Approved = g.Count(x => x.Status == ImportSessionItemStatus.Approved),
                Imported = g.Count(x => x.Status == ImportSessionItemStatus.Imported),
                AlreadyImported = g.Count(x => x.Status == ImportSessionItemStatus.AlreadyImported),
                Failed = g.Count(x => x.Status == ImportSessionItemStatus.Failed)
            })
            .FirstOrDefaultAsync(ct);

        session.TotalItems = counts?.Total ?? 0;
        session.ProbedItems = counts?.Probed ?? 0;
        session.ReadyItems = counts?.Ready ?? 0;
        session.IncompleteItems = counts?.Incomplete ?? 0;
        session.ExcludedItems = counts?.Excluded ?? 0;
        session.ApprovedItems = counts?.Approved ?? 0;
        session.ImportedItems = counts?.Imported ?? 0;
        session.AlreadyImportedItems = counts?.AlreadyImported ?? 0;
        session.FailedItems = counts?.Failed ?? 0;
        session.UpdatedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return ToDto(session);
    }

    private static ImportSessionDto ToDto(ImportSessionEntity x) => new()
    {
        SessionId = x.SessionId,
        CorrelationId = x.CorrelationId,
        Status = x.Status,
        SourceKind = x.SourceKind,
        SourceRoot = x.SourceRoot,
        SubPath = x.SubPath,
        StorageKey = x.StorageKey,
        WorkerTag = x.WorkerTag,
        RequestedBy = x.RequestedBy,
        TotalItems = x.TotalItems,
        ProbedItems = x.ProbedItems,
        ReadyItems = x.ReadyItems,
        IncompleteItems = x.IncompleteItems,
        ExcludedItems = x.ExcludedItems,
        ApprovedItems = x.ApprovedItems,
        ImportedItems = x.ImportedItems,
        AlreadyImportedItems = x.AlreadyImportedItems,
        FailedItems = x.FailedItems,
        MaxParallelItems = x.MaxParallelItems,
        ErrorMessage = x.ErrorMessage,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt,
        CompletedAt = x.CompletedAt
    };

    private static ImportSessionItemDto ToItemDto(ImportSessionItemEntity x) => new()
    {
        ItemId = x.ItemId,
        SessionId = x.SessionId,
        RelativePath = x.RelativePath,
        FileName = x.FileName,
        FileSizeBytes = x.FileSizeBytes,
        FileMtime = x.FileMtime,
        Provider = x.Provider,
        SourceMediaId = x.SourceMediaId,
        SourceUrl = x.SourceUrl,
        Title = x.Title,
        MetadataState = x.MetadataState,
        Excluded = x.Excluded,
        Status = x.Status,
        Attempt = x.Attempt,
        ErrorCode = x.ErrorCode,
        ErrorMessage = x.ErrorMessage,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt,
        CompletedAt = x.CompletedAt
    };

    private static ImportSessionItemWork ToWork(ImportSessionEntity session, ImportSessionItemEntity item) => new()
    {
        SessionId = session.SessionId,
        CorrelationId = session.CorrelationId,
        ItemId = item.ItemId,
        StorageKey = session.StorageKey,
        WorkerTag = session.WorkerTag,
        RelativePath = item.RelativePath,
        FileName = item.FileName,
        SidecarsJson = item.SidecarsJson,
        Provider = item.Provider,
        SourceMediaId = item.SourceMediaId,
        SourceLastModified = item.FileMtime,
        SourceUrl = item.SourceUrl,
        Title = item.Title,
        ProbeMetadataJson = item.ProbeMetadataJson,
        ScanMetadataJson = item.ScanMetadataJson,
        EnrichedMetadataJson = item.EnrichedMetadataJson,
        UserMetadataJson = item.UserMetadataJson,
        MetadataState = item.MetadataState
    };

    private static string NormalizeRequired(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeJson(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ImportSessionWorkProjection(ImportSessionEntity Session, ImportSessionItemEntity Item);
}
