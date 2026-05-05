using System.Text.Json;
using Dashboard.Models;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Shared.Messaging;

namespace Dashboard.Services;

public sealed class NatsJobActivityListener(
    IMessageBus messageBus,
    JobsDashboardState state,
    ILogger<NatsJobActivityListener> logger) : BackgroundService
{
    private ISubscription? _subscription;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await state.SetNatsStatusAsync(false, "Connecting");
            _subscription = await messageBus.SubscribeAsync<JsonElement>(
                DownloadTopology.SubjectFilter,
                HandleAsync,
                cancellationToken: stoppingToken);

            await state.SetNatsStatusAsync(true, $"Subscribed to {DownloadTopology.SubjectFilter}");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NATS dashboard listener stopped unexpectedly");
            await state.SetNatsStatusAsync(false, ex.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.StopAsync(cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(IMessageContext<JsonElement> context)
    {
        var payload = context.Message;
        var activity = new JobActivity(
            Sequence: 0,
            Subject: context.Subject,
            JobId: ReadGuid(payload, "jobId"),
            CorrelationId: ReadGuid(payload, "correlationId"),
            OperationKey: ReadString(payload, "operationKey"),
            Attempt: ReadInt(payload, "attempt"),
            ReceivedAt: DateTimeOffset.UtcNow);

        await state.RecordActivityAsync(activity);
    }

    private static Guid? ReadGuid(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String &&
           Guid.TryParse(property.GetString(), out var value)
            ? value
            : null;

    private static string? ReadString(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
}
