using Microsoft.Extensions.Configuration;
using FrostStream.ApplicationContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nats = Conduit.NATS;

namespace Shared.Messaging.Adapters;

/// <summary>Maps the existing Full transport to the contracts used by application handlers.</summary>
public sealed class NatsApplicationTransport(
    Nats.IMessageBus bus,
    Nats.IJetStreamPublisher publisher,
    Nats.IJetStreamConsumer consumer) : IMessageBus, IDurableJobPublisher, IDurableJobConsumer
{
    private static Nats.MessageHeaders? Headers(MessageHeaders? headers)
        => headers is null ? null : new(headers.Headers);

    public Task PublishAsync<T>(string subject, T message, CancellationToken cancellationToken = default)
        => bus.PublishAsync(subject, message, cancellationToken);

    public Task PublishAsync<T>(string subject, T message, MessageHeaders? headers, CancellationToken cancellationToken = default)
        => bus.PublishAsync(subject, message, Headers(headers), cancellationToken);

    public Task<TResponse?> RequestAsync<TRequest, TResponse>(string subject, TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        => bus.RequestAsync<TRequest, TResponse>(subject, request, timeout, cancellationToken);

    public async Task<ISubscription> SubscribeAsync<T>(string subject, Func<IMessageContext<T>, Task> handler, string? queueGroup = null, CancellationToken cancellationToken = default)
        => new Subscription(await bus.SubscribeAsync<T>(subject, context => handler(new MessageContext<T>(context)), queueGroup, cancellationToken));

    public Task PublishAsync<T>(string subject, T message, string? messageId, MessageHeaders? headers = null, CancellationToken cancellationToken = default)
        => publisher.PublishAsync(subject, message, messageId, Headers(headers), cancellationToken);

    public Task PublishBatchAsync<T>(IReadOnlyList<BatchMessage<T>> messages, CancellationToken cancellationToken = default)
        => publisher.PublishBatchAsync(messages.Select(m => new Nats.BatchMessage<T>(m.Subject, m.Message, m.MessageId, Headers(m.Headers))).ToArray(), cancellationToken);

    public Task ConsumeAsync<T>(StreamName stream, SubjectName subject, Func<IDurableMessageContext<T>, Task> handler, DurableConsumeOptions? options = null, CancellationToken cancellationToken = default)
        => consumer.ConsumeAsync<T>(Nats.StreamName.From(stream.Value), Nats.SubjectName.From(subject.Value), context => handler(new DurableContext<T>(context)), Options(options), cancellationToken);

    public Task ConsumePullAsync<T>(StreamName stream, ConsumerName consumerName, Func<IDurableMessageContext<T>, Task> handler, DurableConsumeOptions? options = null, CancellationToken cancellationToken = default)
        => consumer.ConsumePullAsync<T>(Nats.StreamName.From(stream.Value), Nats.ConsumerName.From(consumerName.Value), context => handler(new DurableContext<T>(context)), Options(options), cancellationToken);

    private static Nats.JetStreamConsumeOptions? Options(DurableConsumeOptions? options) => options is null ? null : new()
    {
        DurableName = options.DurableName is { } durable ? Nats.ConsumerName.From(durable.Value) : null,
        DeliverGroup = options.DeliverGroup is { } group ? Nats.QueueGroup.From(group.Value) : null,
        MaxConcurrency = options.MaxConcurrency,
        BatchSize = options.BatchSize,
        MaxAckPending = options.MaxAckPending
    };

    private sealed class Subscription(Nats.ISubscription subscription) : ISubscription
    {
        public Guid Id => subscription.Id;
        public Task StopAsync(CancellationToken cancellationToken = default) => subscription.StopAsync(cancellationToken);
        public ValueTask DisposeAsync() => subscription.DisposeAsync();
    }

    private class MessageContext<T>(Nats.IMessageContext<T> context) : IMessageContext<T>
    {
        public T Message => context.Message;
        public string Subject => context.Subject;
        public MessageHeaders Headers => new(context.Headers.Headers);
        public string? ReplyTo => context.ReplyTo;
        public Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default)
            => context.RespondAsync(response, cancellationToken);
    }

    private sealed class DurableContext<T>(Nats.IJsMessageContext<T> context) : MessageContext<T>(context), IDurableMessageContext<T>
    {
        public Task AckAsync(CancellationToken cancellationToken = default) => context.AckAsync(cancellationToken);
        public Task NackAsync(TimeSpan? delay = null, CancellationToken cancellationToken = default) => context.NackAsync(delay, cancellationToken);
        public Task TermAsync(CancellationToken cancellationToken = default) => context.TermAsync(cancellationToken);
        public Task InProgressAsync(CancellationToken cancellationToken = default) => context.InProgressAsync(cancellationToken);
        public ulong Sequence => context.Sequence;
        public DateTimeOffset Timestamp => context.Timestamp;
        public bool Redelivered => context.Redelivered;
        public uint NumDelivered => context.NumDelivered;
    }
}

public sealed class NatsStagedObjectStore(Conduit.NATS.IObjectStore store) : IStagedObjectStore
{
    public Task<string> PutAsync(string key, Stream data, CancellationToken cancellationToken = default) => store.PutAsync(key, data, cancellationToken);
    public Task GetAsync(string key, Stream target, CancellationToken cancellationToken = default) => store.GetAsync(key, target, cancellationToken);
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => store.DeleteAsync(key, cancellationToken);
    public ValueTask DisposeAsync() => store.DisposeAsync();
}

public static class ApplicationTransportRegistration
{
    public static IServiceCollection AddApplicationTransport(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        return Shared.Deployment.DeploymentOptions.FromConfiguration(configuration).Mode switch
        {
            Shared.Deployment.DeploymentMode.Full => services.AddNatsApplicationTransport(),
            Shared.Deployment.DeploymentMode.Lite => services.AddLocalApplicationTransport(configuration),
            _ => throw new InvalidOperationException("Unsupported deployment mode.")
        };
    }

    public static IServiceCollection AddLocalApplicationTransport(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var capacity = configuration.GetValue<int?>("Messaging:Local:SubscriptionCapacity") ?? 256;
        if (capacity <= 0) throw new InvalidOperationException("Messaging:Local:SubscriptionCapacity must be positive.");
        // SQLite persistence replaces durable jobs and worker routes in Lite (3b).
        // Staged objects retain their existing adapter until 3c.
        services.AddNatsApplicationTransport();
        services.TryAddSingleton<LocalApplicationTransport>(sp => new(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalApplicationTransport>>(), capacity));
        services.Replace(ServiceDescriptor.Singleton<IMessageBus>(sp => sp.GetRequiredService<LocalApplicationTransport>()));
        return services;
    }

    public static IServiceCollection AddNatsApplicationTransport(this IServiceCollection services)
    {
        services.TryAddSingleton<NatsApplicationTransport>();
        services.TryAddSingleton<IWorkerJobRoutes, NatsWorkerJobRoutes>();
        services.TryAddSingleton<IMessageBus>(sp => sp.GetRequiredService<NatsApplicationTransport>());
        services.TryAddSingleton<IRequestDispatcher>(sp => sp.GetRequiredService<IMessageBus>());
        services.TryAddSingleton<IEventBus>(sp => sp.GetRequiredService<IMessageBus>());
        services.TryAddSingleton<IDurableJobPublisher>(sp => sp.GetRequiredService<NatsApplicationTransport>());
        services.TryAddSingleton<IDurableJobConsumer>(sp => sp.GetRequiredService<NatsApplicationTransport>());
        services.TryAddSingleton<Func<string, IStagedObjectStore>>(sp =>
        {
            var factory = sp.GetRequiredService<Func<string, Conduit.NATS.IObjectStore>>();
            return bucket => new NatsStagedObjectStore(factory(bucket));
        });
        return services;
    }
}
