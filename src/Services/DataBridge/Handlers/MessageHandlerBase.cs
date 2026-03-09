using DataBridge.Data;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataBridge.Handlers;

/// <summary>
/// Base class for DataBridge message handlers that subscribe to NATS subjects.
/// Provides common infrastructure for subscription management, scope creation, and logging.
/// </summary>
/// <typeparam name="TRequest">The type of request message</typeparam>
/// <typeparam name="TResponse">The type of response message</typeparam>
public abstract class MessageHandlerBase<TRequest, TResponse> : BackgroundService
    where TRequest : class
    where TResponse : class
{
    protected readonly IMessageBus MessageBus;
    protected readonly IServiceScopeFactory ScopeFactory;
    protected readonly ILogger Logger;

    /// <summary>
    /// The NATS subject to subscribe to. Must be implemented by derived classes.
    /// </summary>
    protected abstract string Subject { get; }

    /// <summary>
    /// The queue group for load balancing. Defaults to "databridge-jobs".
    /// </summary>
    protected virtual string QueueGroup => "databridge-jobs";

    protected MessageHandlerBase(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        ScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("{HandlerName} subscribing to {Subject}", GetType().Name, Subject);

        await MessageBus.SubscribeAsync<TRequest>(
            Subject,
            async context =>
            {
                var request = context.Message;
                Logger.LogDebug("Received {RequestType} on {Subject}", typeof(TRequest).Name, Subject);

                try
                {
                    using var scope = ScopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                    var response = await HandleRequestAsync(db, request, stoppingToken);
                    await context.RespondAsync(response, stoppingToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error handling {RequestType} on {Subject}", typeof(TRequest).Name, Subject);
                    var errorResponse = CreateErrorResponse(ex);
                    await context.RespondAsync(errorResponse, stoppingToken);
                }
            },
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);
    }

    /// <summary>
    /// Handles the incoming request and returns a response.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="db">The database context (scoped)</param>
    /// <param name="request">The incoming request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The response to send back</returns>
    protected abstract Task<TResponse> HandleRequestAsync(
        FrostStreamDbContext db,
        TRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an error response when an exception occurs.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="exception">The exception that occurred</param>
    /// <returns>An error response</returns>
    protected abstract TResponse CreateErrorResponse(Exception exception);
}

/// <summary>
/// Base class for DataBridge message handlers that don't require a response (one-way).
/// </summary>
/// <typeparam name="TRequest">The type of request message</typeparam>
public abstract class OneWayMessageHandlerBase<TRequest> : BackgroundService
    where TRequest : class
{
    protected readonly IMessageBus MessageBus;
    protected readonly IServiceScopeFactory ScopeFactory;
    protected readonly ILogger Logger;

    /// <summary>
    /// The NATS subject to subscribe to. Must be implemented by derived classes.
    /// </summary>
    protected abstract string Subject { get; }

    /// <summary>
    /// The queue group for load balancing. Defaults to "databridge-jobs".
    /// </summary>
    protected virtual string QueueGroup => "databridge-jobs";

    protected OneWayMessageHandlerBase(
        IMessageBus messageBus,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
    {
        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        ScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("{HandlerName} subscribing to {Subject}", GetType().Name, Subject);

        await MessageBus.SubscribeAsync<TRequest>(
            Subject,
            async context =>
            {
                var request = context.Message;
                Logger.LogDebug("Received {RequestType} on {Subject}", typeof(TRequest).Name, Subject);

                try
                {
                    using var scope = ScopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FrostStreamDbContext>();

                    await HandleRequestAsync(db, request, stoppingToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error handling {RequestType} on {Subject}", typeof(TRequest).Name, Subject);
                    // One-way handlers don't respond, so just log the error
                    throw;
                }
            },
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);
    }

    /// <summary>
    /// Handles the incoming request.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="db">The database context (scoped)</param>
    /// <param name="request">The incoming request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task HandleRequestAsync(
        FrostStreamDbContext db,
        TRequest request,
        CancellationToken cancellationToken);
}
