using DataBridge.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Shared.Messaging;

namespace DataBridge.Flows;

/// <summary>
/// Singleton that serialises download dispatch in priority order across all Cleipnir flows.
///
/// Before a <see cref="DownloadArchiveFlow"/> dispatches a <c>DownloadVideoCommand</c> it
/// calls <see cref="EnqueueAsync"/> and suspends on <see cref="DownloadSlotGranted"/>. This
/// coordinator keeps one sorted pending list per worker tag and grants the slot to the
/// highest-priority waiter (priority DESC, created_at ASC). When the flow finishes (or fails
/// terminally) it calls <see cref="ReleaseSlotAsync"/> so the next job can proceed.
///
/// Restart recovery: on <see cref="StartAsync"/> the coordinator queries all
/// <see cref="DownloadJobState.DownloadQueued"/> rows from the DB and re-populates its
/// waiting lists, so Cleipnir flows that were suspended at <c>await Message&lt;DownloadSlotGranted&gt;()</c>
/// are re-served after a DataBridge restart.
/// </summary>
public sealed class DownloadSlotCoordinator(
    DownloadArchiveFlows flows,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<DownloadSlotCoordinator> logger) : IHostedService
{
    // Per-worker-tag waiting queues, sorted by (priority DESC, createdAt ASC).
    private readonly Dictionary<string, SortedSet<SlotEntry>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    // Tags that currently have an active download in flight.
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DownloadQueuedEntry> pending;
        using (var scope = scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDownloadJobsRepository>();
            pending = await repo.GetDownloadQueuedJobsAsync(cancellationToken);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in pending)
            {
                var tag = NormalizeTag(entry.StorageKey);
                if (!_waiting.TryGetValue(tag, out var set))
                    _waiting[tag] = set = new SortedSet<SlotEntry>(SlotEntryComparer.Instance);
                set.Add(new SlotEntry(entry.JobId, entry.CorrelationId, entry.Priority, entry.CreatedAt));
            }
            logger.LogInformation(
                "DownloadSlotCoordinator recovered {Count} queued jobs across {Tags} worker tag(s).",
                pending.Count,
                _waiting.Count);
        }
        finally
        {
            _lock.Release();
        }

        // Grant any immediately available slots (e.g. no active downloads at start).
        foreach (var tag in _waiting.Keys.ToList())
            await TryGrantNextAsync(tag);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Called by the flow before it enters the download step. Adds the job to the waiting
    /// list and immediately grants the slot if no other download is active for this tag.
    /// </summary>
    public async Task EnqueueAsync(Guid jobId, Guid correlationId, int priority, string? workerTag, Instant requestedAt)
    {
        var tag = NormalizeTag(workerTag);
        await _lock.WaitAsync();
        try
        {
            if (!_waiting.TryGetValue(tag, out var set))
                _waiting[tag] = set = new SortedSet<SlotEntry>(SlotEntryComparer.Instance);
            set.Add(new SlotEntry(jobId, correlationId, priority, requestedAt));
            logger.LogDebug(
                "DownloadSlotCoordinator enqueued JobId {JobId} Priority {Priority} Tag '{Tag}'. Queue depth: {Depth}",
                jobId, priority, tag, set.Count);
        }
        finally
        {
            _lock.Release();
        }
        await TryGrantNextAsync(tag);
    }

    /// <summary>
    /// Called by the flow after its download step completes (success or terminal failure).
    /// Releases the slot and grants it to the next highest-priority waiter.
    /// </summary>
    public async Task ReleaseSlotAsync(string? workerTag)
    {
        var tag = NormalizeTag(workerTag);
        await _lock.WaitAsync();
        try
        {
            _active.Remove(tag);
            logger.LogDebug("DownloadSlotCoordinator released slot for Tag '{Tag}'.", tag);
        }
        finally
        {
            _lock.Release();
        }
        await TryGrantNextAsync(tag);
    }

    /// <summary>
    /// Updates a waiting job's priority. No-op if the job is not in the waiting list
    /// (already downloading or finished). Re-evaluates the grant order after the update.
    /// </summary>
    public async Task UpdatePriorityAsync(Guid jobId, int newPriority, string? workerTag)
    {
        var tag = NormalizeTag(workerTag);
        var changed = false;
        await _lock.WaitAsync();
        try
        {
            if (_waiting.TryGetValue(tag, out var set))
            {
                var existing = set.FirstOrDefault(e => e.JobId == jobId);
                if (existing != default)
                {
                    set.Remove(existing);
                    set.Add(existing with { Priority = newPriority });
                    changed = true;
                    logger.LogDebug(
                        "DownloadSlotCoordinator updated priority for JobId {JobId} → {Priority} Tag '{Tag}'.",
                        jobId, newPriority, tag);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
        if (changed)
            await TryGrantNextAsync(tag);
    }

    /// <summary>
    /// Removes a waiting job from the priority queue. No-op if the job has already been granted
    /// a slot or is not queued for this tag.
    /// </summary>
    public async Task<bool> CancelQueuedAsync(Guid jobId, string? workerTag)
    {
        var tag = NormalizeTag(workerTag);
        await _lock.WaitAsync();
        try
        {
            if (!_waiting.TryGetValue(tag, out var set))
                return false;

            var existing = set.FirstOrDefault(e => e.JobId == jobId);
            if (existing == default)
                return false;

            set.Remove(existing);
            logger.LogDebug(
                "DownloadSlotCoordinator removed cancelled JobId {JobId} from Tag '{Tag}'. Queue depth: {Depth}",
                jobId,
                tag,
                set.Count);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task TryGrantNextAsync(string tag)
    {
        SlotEntry? toGrant = null;
        await _lock.WaitAsync();
        try
        {
            if (_active.Contains(tag))
                return;
            if (!_waiting.TryGetValue(tag, out var set) || set.Count == 0)
                return;
            toGrant = set.Min;
            set.Remove(toGrant.Value);
            _active.Add(tag);
        }
        finally
        {
            _lock.Release();
        }

        if (toGrant is null)
            return;

        var granted = toGrant.Value;
        logger.LogInformation(
            "DownloadSlotCoordinator granting slot to JobId {JobId} Priority {Priority} Tag '{Tag}'.",
            granted.JobId, granted.Priority, tag);
        try
        {
            await flows.SendMessage(granted.JobId.ToString("N"), new DownloadSlotGranted
            {
                JobId = granted.JobId,
                CorrelationId = granted.CorrelationId,
                MessageId = Guid.NewGuid(),
                OperationKey = $"job/{granted.JobId:N}/slot-granted",
                OccurredAt = clock.GetCurrentInstant(),
                WorkerTag = tag
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "DownloadSlotCoordinator failed to send DownloadSlotGranted to flow for JobId {JobId}; re-enqueueing.",
                granted.JobId);
            // Put it back so it will be retried on the next release.
            await _lock.WaitAsync();
            try
            {
                _active.Remove(tag);
                if (!_waiting.TryGetValue(tag, out var set))
                    _waiting[tag] = set = new SortedSet<SlotEntry>(SlotEntryComparer.Instance);
                set.Add(granted);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private static string NormalizeTag(string? tag)
        => string.IsNullOrWhiteSpace(tag) ? string.Empty : tag.Trim();

    private record struct SlotEntry(Guid JobId, Guid CorrelationId, int Priority, Instant CreatedAt);

    private sealed class SlotEntryComparer : IComparer<SlotEntry>
    {
        public static readonly SlotEntryComparer Instance = new();

        public int Compare(SlotEntry x, SlotEntry y)
        {
            // Higher priority first.
            var c = y.Priority.CompareTo(x.Priority);
            if (c != 0) return c;
            // Earlier creation first among equal priorities.
            c = x.CreatedAt.CompareTo(y.CreatedAt);
            if (c != 0) return c;
            // Tie-break on JobId so equal-priority, same-instant jobs are stable.
            return x.JobId.CompareTo(y.JobId);
        }
    }
}
