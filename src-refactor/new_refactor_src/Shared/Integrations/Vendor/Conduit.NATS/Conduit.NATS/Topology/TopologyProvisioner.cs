using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Conduit.NATS;

/// <summary>
/// Hosted service that provisions every registered <see cref="ITopologySource"/>'s streams, consumers,
/// KV buckets, and object stores on startup. Waits for the connection with exponential backoff (no Polly),
/// continues past individual resource failures, and signals <see cref="ITopologyReadySignal"/> when done.
/// </summary>
internal sealed class TopologyProvisioner : IHostedService
{
    private readonly IEnumerable<ITopologySource> _sources;
    private readonly ITopologyManager _manager;
    private readonly NatsConnection _connection;
    private readonly ITopologyReadySignal _readySignal;
    private readonly ILogger<TopologyProvisioner> _logger;
    private readonly ConduitNatsOptions _options;

    public TopologyProvisioner(
        IEnumerable<ITopologySource> sources,
        ITopologyManager manager,
        NatsConnection connection,
        ITopologyReadySignal readySignal,
        ILogger<TopologyProvisioner> logger,
        IOptions<ConduitNatsOptions> options)
    {
        _sources = sources;
        _manager = manager;
        _connection = connection;
        _readySignal = readySignal;
        _logger = logger;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting topology provisioning...");
        try
        {
            await WaitForConnectionAsync(cancellationToken);

            var sources = _sources.ToList();
            var streams = sources.SelectMany(s => s.GetStreams()).ToList();
            var consumers = sources.SelectMany(s => s.GetConsumers()).ToList();
            var buckets = sources.SelectMany(s => s.GetBuckets()).ToList();
            var objectStores = sources.SelectMany(s => s.GetObjectStores()).ToList();

            _logger.LogInformation(
                "Provisioning {Streams} stream(s), {Consumers} consumer(s), {Buckets} bucket(s), {ObjectStores} object store(s)...",
                streams.Count, consumers.Count, buckets.Count, objectStores.Count);

            var failures = new List<Exception>();

            foreach (var bucket in buckets)
                await ProvisionAsync(() => _manager.EnsureBucketAsync(bucket, cancellationToken), $"KV bucket {bucket.Name}", failures, cancellationToken);

            foreach (var store in objectStores)
                await ProvisionAsync(() => _manager.EnsureObjectStoreAsync(store, cancellationToken), $"object store {store.Name}", failures, cancellationToken);

            foreach (var stream in streams)
                await ProvisionAsync(() => _manager.EnsureStreamAsync(stream, cancellationToken), $"stream {stream.Name}", failures, cancellationToken);

            foreach (var consumer in consumers)
                await ProvisionAsync(() => _manager.EnsureConsumerAsync(consumer, cancellationToken), $"consumer {consumer.StreamName}/{consumer.DurableName}", failures, cancellationToken);

            if (failures.Count > 0)
            {
                _logger.LogError("Topology provisioning completed with {Count} failure(s).", failures.Count);
                _readySignal.SignalFailed(new AggregateException("Topology provisioning partially failed.", failures));
            }
            else
            {
                _logger.LogInformation("Topology provisioning completed.");
                _readySignal.SignalReady();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Topology provisioning failed with an unrecoverable error.");
            _readySignal.SignalFailed(ex);
            throw;
        }
    }

    private async Task ProvisionAsync(Func<Task> action, string description, List<Exception> failures, CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to provision {Resource}. Continuing with remaining topology.", description);
            failures.Add(ex);
        }
    }

    private async Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection.ConnectionState == NatsConnectionState.Open)
            return;

        // The v3 client owns connect + retry (RetryOnInitialConnect). We just await it, bounded by the
        // startup budget so a permanently-down server fails fast instead of blocking host startup forever.
        var connectTask = _connection.ConnectAsync().AsTask();
        try
        {
            if (_options.TotalStartupTimeout > TimeSpan.Zero)
                await connectTask.WaitAsync(_options.TotalStartupTimeout, cancellationToken);
            else
                await connectTask.WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"NATS connection could not be established within {_options.TotalStartupTimeout}. " +
                $"Current state: {_connection.ConnectionState}.");
        }

        _logger.LogInformation("NATS connection established.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
