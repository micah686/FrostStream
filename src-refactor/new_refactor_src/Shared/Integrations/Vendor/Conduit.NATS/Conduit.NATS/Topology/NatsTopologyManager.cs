using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.KeyValueStore;
using NATS.Client.ObjectStore;

namespace Conduit.NATS;

/// <summary>
/// Create-or-update implementation of <see cref="ITopologyManager"/> against the native v3
/// JetStream/KV/ObjectStore APIs. Idempotent via create-first-then-update with existence checks;
/// immutable drift on consumers is surfaced rather than silently recreated.
/// </summary>
internal sealed class NatsTopologyManager : ITopologyManager
{
    private readonly INatsJSContext _jsContext;
    private readonly INatsKVContext _kvContext;
    private readonly INatsObjContext _objContext;
    private readonly ILogger<NatsTopologyManager> _logger;

    public NatsTopologyManager(
        INatsJSContext jsContext,
        INatsKVContext kvContext,
        INatsObjContext objContext,
        ILogger<NatsTopologyManager> logger)
    {
        _jsContext = jsContext;
        _kvContext = kvContext;
        _objContext = objContext;
        _logger = logger;
    }

    public async Task EnsureStreamAsync(StreamSpec spec, CancellationToken cancellationToken = default)
    {
        var maxMsgSize = spec.MaxMsgSize == -1 ? -1 : (spec.MaxMsgSize > int.MaxValue ? int.MaxValue : (int)spec.MaxMsgSize);

        var config = new StreamConfig(spec.Name.Value, spec.Subjects)
        {
            MaxBytes = spec.MaxBytes,
            MaxMsgSize = maxMsgSize,
            MaxAge = spec.MaxAge,
            Storage = spec.StorageType == StorageType.Memory ? StreamConfigStorage.Memory : StreamConfigStorage.File,
            Retention = spec.RetentionPolicy switch
            {
                StreamRetention.Limits => StreamConfigRetention.Limits,
                StreamRetention.Interest => StreamConfigRetention.Interest,
                StreamRetention.WorkQueue => StreamConfigRetention.Workqueue,
                _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.RetentionPolicy, "Unknown retention policy")
            },
            NumReplicas = spec.Replicas
        };

        _logger.LogInformation("Ensuring stream {Stream}...", spec.Name);
        var existing = await GetOrDefaultAsync(() => _jsContext.GetStreamAsync(spec.Name.Value, cancellationToken: cancellationToken));

        if (existing == null)
        {
            await _jsContext.CreateStreamAsync(config, cancellationToken);
            _logger.LogInformation("Stream {Stream} created.", spec.Name);
        }
        else if (StreamConfigChanged(existing.Info.Config, config))
        {
            ValidateStreamUpdate(existing.Info.Config, config);
            await _jsContext.UpdateStreamAsync(config, cancellationToken);
            _logger.LogInformation("Stream {Stream} updated.", spec.Name);
        }
        else
        {
            _logger.LogDebug("Stream {Stream} already up to date.", spec.Name);
        }
    }

    public async Task EnsureConsumerAsync(ConsumerSpec spec, CancellationToken cancellationToken = default)
    {
        var filterSubjects = spec.GetFilterSubjects();
        var config = new ConsumerConfig
        {
            Name = spec.DurableName.Value,
            DurableName = spec.DurableName.Value,
            Description = spec.Description,
            FilterSubject = filterSubjects.Count > 1 ? null : filterSubjects.FirstOrDefault(),
            FilterSubjects = filterSubjects.Count > 1 ? filterSubjects.ToList() : null,
            AckPolicy = spec.AckPolicy switch
            {
                AckPolicy.None => ConsumerConfigAckPolicy.None,
                AckPolicy.All => ConsumerConfigAckPolicy.All,
                AckPolicy.Explicit => ConsumerConfigAckPolicy.Explicit,
                _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.AckPolicy, "Unknown ack policy")
            },
            AckWait = spec.AckWait,
            MaxDeliver = spec.MaxDeliver,
            DeliverPolicy = spec.DeliverPolicy switch
            {
                DeliverPolicy.All => ConsumerConfigDeliverPolicy.All,
                DeliverPolicy.Last => ConsumerConfigDeliverPolicy.Last,
                DeliverPolicy.New => ConsumerConfigDeliverPolicy.New,
                DeliverPolicy.ByStartSequence => ConsumerConfigDeliverPolicy.ByStartSequence,
                DeliverPolicy.ByStartTime => ConsumerConfigDeliverPolicy.ByStartTime,
                DeliverPolicy.LastPerSubject => ConsumerConfigDeliverPolicy.LastPerSubject,
                _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.DeliverPolicy, "Unknown deliver policy")
            },
            Backoff = spec.Backoff,
            DeliverGroup = spec.DeliverGroup?.Value
        };
        if (spec.MaxAckPending.HasValue)
            config.MaxAckPending = spec.MaxAckPending.Value;

        _logger.LogInformation("Ensuring consumer {Stream}/{Consumer}...", spec.StreamName, spec.DurableName);
        try
        {
            await _jsContext.CreateOrUpdateConsumerAsync(spec.StreamName.Value, config, cancellationToken);
        }
        catch (NatsJSApiException ex) when (IsImmutablePropertyError(ex))
        {
            throw new InvalidOperationException(
                $"Consumer {spec.StreamName.Value}/{spec.DurableName.Value} has immutable topology drift " +
                $"(JetStream error {ex.Error.ErrCode}: {ex.Error.Description}). " +
                "Refusing to recreate automatically; apply the change via an operator workflow.", ex);
        }
        _logger.LogInformation("Consumer {Stream}/{Consumer} ready.", spec.StreamName, spec.DurableName);
    }

    public Task EnsureBucketAsync(BucketName name, StorageType storageType, CancellationToken cancellationToken = default)
        => EnsureBucketAsync(new BucketSpec { Name = name, StorageType = storageType }, cancellationToken);

    public async Task EnsureBucketAsync(BucketSpec spec, CancellationToken cancellationToken = default)
    {
        var config = new NatsKVConfig(spec.Name.Value)
        {
            Storage = spec.StorageType == StorageType.Memory ? NatsKVStorageType.Memory : NatsKVStorageType.File,
            MaxBytes = spec.MaxBytes,
            History = spec.History,
            Description = spec.Description,
            MaxAge = spec.MaxAge
        };

        try
        {
            await _kvContext.CreateStoreAsync(config, cancellationToken: cancellationToken);
            _logger.LogInformation("KV bucket {Bucket} created.", spec.Name);
        }
        catch (NatsJSApiException ex) when (IsResourceExistsError(ex))
        {
            _logger.LogDebug("KV bucket {Bucket} already exists.", spec.Name);
        }
    }

    public Task EnsureObjectStoreAsync(BucketName name, StorageType storageType, CancellationToken cancellationToken = default)
        => EnsureObjectStoreAsync(new ObjectStoreSpec { Name = name, StorageType = storageType }, cancellationToken);

    public async Task EnsureObjectStoreAsync(ObjectStoreSpec spec, CancellationToken cancellationToken = default)
    {
        var config = new NatsObjConfig(spec.Name.Value)
        {
            Storage = spec.StorageType == StorageType.Memory ? NatsObjStorageType.Memory : NatsObjStorageType.File,
            MaxBytes = spec.MaxBytes,
            MaxAge = spec.MaxAge > TimeSpan.Zero ? spec.MaxAge : TimeSpan.Zero,
            Description = spec.Description
        };

        try
        {
            await _objContext.CreateObjectStoreAsync(config, cancellationToken: cancellationToken);
            _logger.LogInformation("Object store {Bucket} created.", spec.Name);
        }
        catch (NatsJSApiException ex) when (IsResourceExistsError(ex))
        {
            _logger.LogDebug("Object store {Bucket} already exists.", spec.Name);
        }
    }

    private static async Task<T?> GetOrDefaultAsync<T>(Func<ValueTask<T>> getter) where T : class
    {
        try
        {
            return await getter();
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            return null;
        }
    }

    private static bool IsResourceExistsError(NatsJSApiException ex) =>
        ex.Error.Code == 400 &&
        (ex.Error.ErrCode == 10058 /* stream exists */ ||
         ex.Error.ErrCode == 10148 /* consumer exists */ ||
         ex.Error.Description?.Contains("already", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsImmutablePropertyError(NatsJSApiException ex)
    {
        if (ex.Error.ErrCode is 10058 or 10148)
            return true;
        var description = ex.Error.Description?.ToLowerInvariant() ?? string.Empty;
        return description.Contains("can not be updated")
            || description.Contains("cannot be updated")
            || description.Contains("immutable");
    }

    private void ValidateStreamUpdate(StreamConfig existing, StreamConfig desired)
    {
        if (existing.Storage != desired.Storage)
            throw new InvalidOperationException($"Stream storage type cannot be changed from {existing.Storage} to {desired.Storage}.");
        if (existing.Retention != desired.Retention)
            throw new InvalidOperationException($"Stream retention policy cannot be changed from {existing.Retention} to {desired.Retention}.");
        if (existing.NumReplicas != desired.NumReplicas)
            throw new InvalidOperationException($"Stream replica count cannot be changed from {existing.NumReplicas} to {desired.NumReplicas}.");
        if (existing.MaxBytes != desired.MaxBytes)
            _logger.LogWarning("Changing stream MaxBytes from {Old} to {New}", existing.MaxBytes, desired.MaxBytes);
    }

    private static bool StreamConfigChanged(StreamConfig existing, StreamConfig desired)
    {
        if (existing.Storage != desired.Storage) return true;
        if (existing.Retention != desired.Retention) return true;
        if (existing.NumReplicas != desired.NumReplicas) return true;
        if (existing.MaxBytes != desired.MaxBytes) return true;
        if (existing.MaxAge != desired.MaxAge) return true;
        if (existing.MaxMsgSize != desired.MaxMsgSize) return true;

        var existingSubjects = existing.Subjects ?? new List<string>();
        var desiredSubjects = desired.Subjects ?? new List<string>();
        if (existingSubjects.Count != desiredSubjects.Count) return true;
        return !new HashSet<string>(existingSubjects).SetEquals(desiredSubjects);
    }
}
