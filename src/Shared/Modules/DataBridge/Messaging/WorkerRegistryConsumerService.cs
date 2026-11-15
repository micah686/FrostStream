using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataBridge.Data;
using DataBridge.Persistence;
using Shared.Database;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class WorkerRegistryConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    NodaTime.IClock clock) : SubscriptionBackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<string, WorkerInfo> workers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly NodaTime.Duration CollectionLeaseDuration = NodaTime.Duration.FromMinutes(2);

    public IReadOnlyList<WorkerInfo> List(string? tag = null)
    {
        lock (gate)
            return workers.Values.Where(w => string.IsNullOrWhiteSpace(tag) || w.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).OrderBy(w => w.Name).ToArray();
    }

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<WorkerHeartbeat>(messageBus, WorkerRegistrySubjects.Heartbeat, HandleHeartbeatAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<WorkerRegistryListRequest>(messageBus, WorkerRegistrySubjects.List, HandleListAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<WorkerCapacityUpdateRequest>(messageBus, WorkerRegistrySubjects.UpdateCapacity,
            HandleUpdateCapacityAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<CollectionCapacityRequest>(messageBus, WorkerRegistrySubjects.AcquireCollection,
            HandleAcquireCollectionAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<CollectionCapacityRequest>(messageBus, WorkerRegistrySubjects.RenewCollection,
            HandleRenewCollectionAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<CollectionCapacityRequest>(messageBus, WorkerRegistrySubjects.ReleaseCollection,
            HandleReleaseCollectionAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
    }

    private Task HandleHeartbeatAsync(FrostStream.ApplicationContracts.IMessageContext<WorkerHeartbeat> context)
    {
        var heartbeat = context.Message;
        lock (gate)
            workers[heartbeat.WorkerId] = new WorkerInfo { WorkerId = heartbeat.WorkerId, Name = heartbeat.Name, Tags = heartbeat.Tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(), IncomingRoot = heartbeat.IncomingRoot, LastOnline = heartbeat.ReportedAt };
        return Task.CompletedTask;
    }

    private async Task HandleListAsync(FrostStream.ApplicationContracts.IMessageContext<WorkerRegistryListRequest> context)
    {
        var registered = List(context.Message.Tag);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var ids = registered.Select(x => x.WorkerId).ToArray();
        var capacities = await db.WorkerCapacities.AsNoTracking()
            .Where(x => ids.Contains(x.WorkerId))
            .ToDictionaryAsync(x => x.WorkerId, x => x.MaxActiveJobs);
        var now = clock.GetCurrentInstant();
        var active = await db.DownloadWorkerLeases.AsNoTracking()
            .Where(x => ids.Contains(x.WorkerInstanceId)
                && x.Status == DownloadWorkerLeaseStatus.Active && x.ExpiresAt > now)
            .Select(x => new { x.WorkerInstanceId, x.JobId }).Distinct().ToListAsync();
        var collections = await db.CollectionWorkerLeases.AsNoTracking()
            .Where(x => ids.Contains(x.WorkerId) && x.ReleasedAt == null && x.ExpiresAt > now)
            .Select(x => x.WorkerId).ToListAsync();
        await context.RespondAsync(new WorkerRegistryListResponse
        {
            Workers = registered.Select(x => x with
            {
                MaxActiveJobs = capacities.GetValueOrDefault(x.WorkerId, 2),
                ActiveJobs = active.Count(a => a.WorkerInstanceId == x.WorkerId) + collections.Count(id => id == x.WorkerId)
            }).ToArray()
        });
    }

    private async Task HandleUpdateCapacityAsync(FrostStream.ApplicationContracts.IMessageContext<WorkerCapacityUpdateRequest> context)
    {
        var request = context.Message;
        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.MaxActiveJobs is < 1 or > 32)
        {
            await context.RespondAsync(new WorkerCapacityUpdateResponse { Error = "Maximum active jobs must be between 1 and 32." });
            return;
        }
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await db.MutateAsync("WorkerRegistryConsumerService.UpdateCapacity", () =>
            db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql(
                "WorkerRegistryConsumerService.UpdateCapacity.1", request.WorkerId.Trim(), request.MaxActiveJobs)));
        await context.RespondAsync(new WorkerCapacityUpdateResponse { Success = true });
    }

    private async Task HandleAcquireCollectionAsync(IMessageContext<CollectionCapacityRequest> context)
    {
        var response = await AcquireCollectionAsync(context.Message);
        await context.RespondAsync(response);
    }

    internal async Task<CollectionCapacityResponse> AcquireCollectionAsync(CollectionCapacityRequest request)
    {
        if (request.DispatchId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkerId))
            return new CollectionCapacityResponse { Stale = true };
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var response = await db.MutateAsync("WorkerRegistryConsumerService.AcquireCollection", async () =>
        {
            var now = clock.GetCurrentInstant();
            var group = request.GroupId is { } groupId
                ? await db.DownloadGroups.FromSqlInterpolated(db.ParameterizedSql(
                    "WorkerRegistryConsumerService.LockCollectionGroup.1", groupId)).SingleOrDefaultAsync()
                : null;
            if (request.GroupId is not null && (group is null || group.Status is not
                (DownloadGroupStatus.Queued or DownloadGroupStatus.Expanding)))
                return new CollectionCapacityResponse { Stale = true };
            await db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql(
                "DownloadFlowV2Repository.LockWorkerCapacityAsync.1", request.WorkerId));
            await db.Database.ExecuteSqlInterpolatedAsync(db.ParameterizedSql(
                "DownloadFlowV2Repository.LockWorkerCapacityAsync.2", request.WorkerId));
            var existing = await db.CollectionWorkerLeases.FirstOrDefaultAsync(x => x.DispatchId == request.DispatchId);
            if (existing is { ReleasedAt: null } && existing.ExpiresAt > now)
            {
                if (existing.WorkerId != request.WorkerId)
                    return new CollectionCapacityResponse();
                existing.ExpiresAt = now + CollectionLeaseDuration;
                await db.SaveChangesAsync();
                return new CollectionCapacityResponse { Granted = true };
            }
            var capacity = await db.WorkerCapacities.AsNoTracking()
                .Where(x => x.WorkerId == request.WorkerId).Select(x => x.MaxActiveJobs).SingleAsync();
            var activeJobs = await db.DownloadWorkerLeases.AsNoTracking()
                .Where(x => x.WorkerInstanceId == request.WorkerId
                    && x.Status == DownloadWorkerLeaseStatus.Active && x.ExpiresAt > now)
                .Select(x => x.JobId).Distinct().CountAsync();
            var activeCollections = await db.CollectionWorkerLeases.AsNoTracking()
                .CountAsync(x => x.WorkerId == request.WorkerId && x.DispatchId != request.DispatchId
                    && x.ReleasedAt == null && x.ExpiresAt > now);
            if (activeJobs + activeCollections >= capacity)
                return new CollectionCapacityResponse();
            if (existing is null)
                db.CollectionWorkerLeases.Add(new CollectionWorkerLeaseEntity
                { DispatchId = request.DispatchId, WorkerId = request.WorkerId, GroupId = request.GroupId,
                    ExpiresAt = now + CollectionLeaseDuration });
            else
            {
                existing.WorkerId = request.WorkerId;
                existing.ExpiresAt = now + CollectionLeaseDuration;
                existing.ReleasedAt = null;
            }
            if (group is { Status: DownloadGroupStatus.Queued })
            {
                group.Status = DownloadGroupStatus.Expanding;
                group.UpdatedAt = now;
            }
            await db.SaveChangesAsync();
            return new CollectionCapacityResponse { Granted = true };
        });
        return response;
    }

    private async Task HandleRenewCollectionAsync(IMessageContext<CollectionCapacityRequest> context)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        var response = await db.MutateAsync("WorkerRegistryConsumerService.RenewCollection", async () =>
        {
            var request = context.Message;
            var now = clock.GetCurrentInstant();
            var lease = await db.CollectionWorkerLeases.FirstOrDefaultAsync(x => x.DispatchId == request.DispatchId);
            if (lease is null || lease.WorkerId != request.WorkerId || lease.ReleasedAt is not null || lease.ExpiresAt <= now)
                return new CollectionCapacityResponse { Stale = true };
            if (lease.GroupId is { } groupId && !await db.DownloadGroups.AsNoTracking().AnyAsync(x => x.GroupId == groupId
                && (x.Status == DownloadGroupStatus.Queued || x.Status == DownloadGroupStatus.Expanding)))
                return new CollectionCapacityResponse { Stale = true };
            lease.ExpiresAt = now + CollectionLeaseDuration;
            await db.SaveChangesAsync();
            return new CollectionCapacityResponse { Granted = true };
        });
        await context.RespondAsync(response);
    }

    private async Task HandleReleaseCollectionAsync(IMessageContext<CollectionCapacityRequest> context)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataBridgeDbContext>();
        await db.MutateAsync("WorkerRegistryConsumerService.ReleaseCollection", async () =>
        {
            var lease = await db.CollectionWorkerLeases.FirstOrDefaultAsync(x => x.DispatchId == context.Message.DispatchId
                && x.WorkerId == context.Message.WorkerId && x.ReleasedAt == null);
            if (lease is null) return;
            lease.ReleasedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync();
        });
        await context.RespondAsync(new CollectionCapacityResponse { Granted = true });
    }
}
