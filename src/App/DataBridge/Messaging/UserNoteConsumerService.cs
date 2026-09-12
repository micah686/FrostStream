using Conduit.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Application;
using Shared.Messaging;

namespace DataBridge.Messaging;

/// <summary>NATS request/reply adapter for the transport-independent user-note application.</summary>
public sealed class UserNoteConsumerService(
    IMessageBus messageBus,
    IServiceScopeFactory scopeFactory,
    ILogger<UserNoteConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "databridge-user-notes";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        await SubscribeAsync<UserNoteUpsertRequestMessage>(
            messageBus,
            UserNoteSubjects.Upsert,
            context => RespondAsync(context, (application, ct) => application.UpsertAsync(context.Message, ct), stoppingToken),
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<UserNoteGetRequestMessage>(
            messageBus,
            UserNoteSubjects.Get,
            context => RespondAsync(context, (application, ct) => application.GetAsync(context.Message, ct), stoppingToken),
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<UserNoteDeleteRequestMessage>(
            messageBus,
            UserNoteSubjects.Delete,
            context => RespondAsync(context, (application, ct) => application.DeleteAsync(context.Message, ct), stoppingToken),
            QueueGroup,
            stoppingToken);
        await SubscribeAsync<UserNoteSearchRequestMessage>(
            messageBus,
            UserNoteSubjects.Search,
            context => RespondAsync(context, (application, ct) => application.SearchAsync(context.Message, ct), stoppingToken),
            QueueGroup,
            stoppingToken);

        logger.LogInformation("Subscribed NATS adapter to user note subjects.");
    }

    private async Task RespondAsync<TRequest, TResponse>(
        IMessageContext<TRequest> context,
        Func<IUserNoteApplication, CancellationToken, Task<TResponse?>> operation,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var response = await scopeFactory.WithScopedAsync<IUserNoteApplication, TResponse?>(
            application => operation(application, cancellationToken));
        if (response is not null)
            await context.RespondAsync(response, cancellationToken);
    }
}
