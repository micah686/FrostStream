using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace DataBridge.Messaging;

public sealed class WorkerRegistryConsumerService(
    IMessageBus messageBus) : SubscriptionBackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<string, WorkerInfo> workers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WorkerInfo> List(string? tag = null)
    {
        lock (gate)
            return workers.Values.Where(w => string.IsNullOrWhiteSpace(tag) || w.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).OrderBy(w => w.Name).ToArray();
    }

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<WorkerHeartbeat>(messageBus, WorkerRegistrySubjects.Heartbeat, HandleHeartbeatAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
        await SubscribeAsync<WorkerRegistryListRequest>(messageBus, WorkerRegistrySubjects.List, HandleListAsync, WorkerRegistrySubjects.QueueGroup, stoppingToken);
    }

    private Task HandleHeartbeatAsync(Conduit.NATS.IMessageContext<WorkerHeartbeat> context)
    {
        var heartbeat = context.Message;
        lock (gate)
            workers[heartbeat.WorkerId] = new WorkerInfo { WorkerId = heartbeat.WorkerId, Name = heartbeat.Name, Tags = heartbeat.Tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(), IncomingRoot = heartbeat.IncomingRoot, LastOnline = heartbeat.ReportedAt };
        return Task.CompletedTask;
    }

    private Task HandleListAsync(Conduit.NATS.IMessageContext<WorkerRegistryListRequest> context)
        => context.RespondAsync(new WorkerRegistryListResponse { Workers = List(context.Message.Tag) });
}
