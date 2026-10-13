using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Conduit.NATS;

/// <summary>
/// Thin JetStream publish/consume facade over <see cref="INatsJSContext"/>. Publishing requires a
/// caller-supplied message ID for server-side de-duplication; consuming runs a self-healing loop and
/// Naks on handler failure so JetStream redelivers (per the consumer's MaxDeliver/Backoff).
/// </summary>
internal sealed class NatsJetStreamBus : IJetStreamPublisher, IJetStreamConsumer, IAsyncDisposable
{
    private const string MsgIdHeader = "Nats-Msg-Id";

    private readonly INatsJSContext _jsContext;
    private readonly ILogger<NatsJetStreamBus> _logger;
    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Cts)> _consumers = new();
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public NatsJetStreamBus(INatsJSContext jsContext, ILogger<NatsJetStreamBus> logger)
    {
        _jsContext = jsContext;
        _logger = logger;
    }

    public async Task PublishAsync<T>(string subject, T message, string? messageId, MessageHeaders? headers = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException(
                "A messageId must be provided for JetStream publishing to ensure application-level idempotency. " +
                "Use a business-key-derived ID (e.g. 'Order-123-Created') so retries de-duplicate.",
                nameof(messageId));
        }

        var natsHeaders = NatsHeaderConverter.ToNats(headers) ?? new NatsHeaders();
        natsHeaders[MsgIdHeader] = messageId;

        var ack = await _jsContext.PublishAsync(
            subject,
            message,
            headers: natsHeaders,
            opts: new NatsJSPubOpts { MsgId = messageId },
            cancellationToken: cancellationToken);

        // A duplicate is the success case for an idempotent retry: the message is already persisted,
        // so treat it as a no-op rather than letting EnsureSuccess() throw NatsJSDuplicateMessageException.
        if (ack.Duplicate)
        {
            _logger.LogDebug("JetStream de-duplicated publish to {Subject} with MsgId {MsgId} (already stored)", subject, messageId);
            return;
        }

        ack.EnsureSuccess();
        _logger.LogDebug("Published JetStream message to {Subject} with MsgId {MsgId}", subject, messageId);
    }

    public async Task PublishBatchAsync<T>(IReadOnlyList<BatchMessage<T>> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            return;

        for (var i = 0; i < messages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(messages[i].MessageId))
                throw new ArgumentException($"Batch message at index {i} has a null or empty messageId.", nameof(messages));
        }

        var tasks = new Task[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            tasks[i] = PublishAsync(msg.Subject, msg.Message, msg.MessageId, msg.Headers, cancellationToken);
        }

        var exceptions = new List<Exception>();
        for (var i = 0; i < tasks.Length; i++)
        {
            try
            {
                await tasks[i];
            }
            catch (Exception ex)
            {
                exceptions.Add(new InvalidOperationException(
                    $"Batch message at index {i} (MessageId: '{messages[i].MessageId}') failed: {ex.Message}", ex));
            }
        }

        if (exceptions.Count > 0)
            throw new AggregateException($"{exceptions.Count} of {messages.Count} batch message(s) failed to publish.", exceptions);
    }

    public async Task ConsumeAsync<T>(
        StreamName stream,
        SubjectName subject,
        Func<IJsMessageContext<T>, Task> handler,
        JetStreamConsumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? JetStreamConsumeOptions.Default;
        ValidateConsumeOptions(opts);

        INatsJSConsumer consumer;
        if (opts.DurableName is { } durable)
        {
            consumer = await _jsContext.GetConsumerAsync(stream.Value, durable.Value, cancellationToken);
        }
        else
        {
            var config = new ConsumerConfig($"ephemeral_{Guid.NewGuid():N}")
            {
                FilterSubject = subject.Value,
                DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverGroup = opts.DeliverGroup?.Value
            };
            if (opts.MaxAckPending.HasValue)
                config.MaxAckPending = opts.MaxAckPending.Value;
            consumer = await _jsContext.CreateOrUpdateConsumerAsync(stream.Value, config, cancellationToken);
        }

        StartConsumeLoop(consumer, stream.Value, handler, opts, cancellationToken);
    }

    public async Task ConsumePullAsync<T>(
        StreamName stream,
        ConsumerName consumer,
        Func<IJsMessageContext<T>, Task> handler,
        JetStreamConsumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? JetStreamConsumeOptions.Default;
        ValidateConsumeOptions(opts);

        var jsConsumer = await _jsContext.GetConsumerAsync(stream.Value, consumer.Value, cancellationToken);
        StartConsumeLoop(jsConsumer, stream.Value, handler, opts, cancellationToken);
    }

    private void StartConsumeLoop<T>(
        INatsJSConsumer consumer,
        string stream,
        Func<IJsMessageContext<T>, Task> handler,
        JetStreamConsumeOptions opts,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linkedCts.Token;
        var consumerName = consumer.Info.Config.Name ?? consumer.Info.Name ?? "unknown";

        var task = Task.Run(() => ConsumeLoopAsync(consumer, stream, consumerName, handler, opts, token), token);
        _consumers[id] = (task, linkedCts);
    }

    private async Task ConsumeLoopAsync<T>(
        INatsJSConsumer consumer,
        string stream,
        string consumerName,
        Func<IJsMessageContext<T>, Task> handler,
        JetStreamConsumeOptions opts,
        CancellationToken token)
    {
        var batchSize = opts.BatchSize > 0 ? opts.BatchSize : 10;
        var concurrency = opts.MaxConcurrency is > 0 ? new SemaphoreSlim(opts.MaxConcurrency.Value) : null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var consumeOpts = new NatsJSConsumeOpts { MaxMsgs = batchSize, Expires = TimeSpan.FromSeconds(30) };
                await foreach (var msg in consumer.ConsumeAsync<T>(opts: consumeOpts, cancellationToken: token))
                {
                    if (concurrency != null)
                        await concurrency.WaitAsync(token);

                    try
                    {
                        await handler(new JsMessageContext<T>(msg, _jsContext.Connection));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message from {Stream}/{Consumer}", stream, consumerName);
                        try
                        {
                            await msg.NakAsync(delay: TimeSpan.FromSeconds(5), cancellationToken: token);
                        }
                        catch (Exception nakEx)
                        {
                            _logger.LogWarning(nakEx, "Failed to NAK message from {Stream}/{Consumer}", stream, consumerName);
                        }
                    }
                    finally
                    {
                        concurrency?.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consumer loop error for {Stream}/{Consumer}; retrying in 1s", stream, consumerName);
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch (OperationCanceledException) { break; }
            }
        }

        concurrency?.Dispose();
    }

    private static void ValidateConsumeOptions(JetStreamConsumeOptions options)
    {
        if (options.MaxConcurrency is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be greater than zero when specified.");
        if (options.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be greater than zero.");
        if (options.MaxAckPending is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAckPending must be greater than zero when specified.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _cts.CancelAsync();

        foreach (var id in _consumers.Keys)
        {
            if (_consumers.TryRemove(id, out var entry))
            {
                try { await entry.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                entry.Cts.Dispose();
            }
        }

        _cts.Dispose();
    }
}

internal sealed class JsMessageContext<T> : IJsMessageContext<T>
{
    private readonly INatsJSMsg<T> _msg;
    private readonly INatsConnection _connection;

    public JsMessageContext(INatsJSMsg<T> msg, INatsConnection connection)
    {
        _msg = msg;
        _connection = connection;
    }

    public T Message => _msg.Data ?? throw new InvalidOperationException($"JetStream message on '{_msg.Subject}' had a null payload.");
    public string Subject => _msg.Subject;
    public MessageHeaders Headers => NatsHeaderConverter.FromNats(_msg.Headers);
    public string? ReplyTo => _msg.ReplyTo;

    public Task AckAsync(CancellationToken cancellationToken = default) => _msg.AckAsync(cancellationToken: cancellationToken).AsTask();
    public Task NackAsync(TimeSpan? delay = null, CancellationToken cancellationToken = default) => _msg.NakAsync(delay: delay ?? TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).AsTask();
    public Task TermAsync(CancellationToken cancellationToken = default) => _msg.AckTerminateAsync(cancellationToken: cancellationToken).AsTask();
    public Task InProgressAsync(CancellationToken cancellationToken = default) => _msg.AckProgressAsync(cancellationToken: cancellationToken).AsTask();

    public ulong Sequence => _msg.Metadata?.Sequence.Stream ?? 0;
    public DateTimeOffset Timestamp => _msg.Metadata?.Timestamp ?? DateTimeOffset.UtcNow;
    public bool Redelivered => (_msg.Metadata?.NumDelivered ?? 1) > 1;
    public uint NumDelivered => (uint)(_msg.Metadata?.NumDelivered ?? 1);

    public async Task RespondAsync<TResponse>(TResponse response, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ReplyTo))
            throw new InvalidOperationException("Message does not have a ReplyTo subject.");
        await _connection.PublishAsync(ReplyTo, response, cancellationToken: cancellationToken);
    }
}
