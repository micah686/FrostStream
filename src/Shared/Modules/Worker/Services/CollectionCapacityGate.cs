using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Logging;
using Shared.Messaging;

namespace Worker.Services;

/// <summary>Holds one worker slot before collection discovery can invoke yt-dlp.</summary>
public sealed class CollectionCapacityGate(
    IMessageBus messageBus,
    WorkerInstanceIdentity identity,
    ILogger<CollectionCapacityGate> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

    public async Task<Permit?> AcquireAsync<T>(IDurableMessageContext<T> context, Guid dispatchId,
        Guid? groupId, CancellationToken stoppingToken) where T : class
    {
        var request = new CollectionCapacityRequest
        { DispatchId = dispatchId, GroupId = groupId, WorkerId = identity.Id };
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var progress = JetStreamHeartbeat.RunAsync(context, RenewInterval, logger,
            "Collection capacity", lifetime.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var response = await messageBus.RequestAsync<CollectionCapacityRequest, CollectionCapacityResponse>(
                        WorkerRegistrySubjects.AcquireCollection, request, RequestTimeout, stoppingToken);
                    if (response?.Granted == true)
                        return new Permit(messageBus, request, lifetime, progress, logger);
                    if (response?.Stale == true)
                    {
                        await lifetime.CancelAsync();
                        await progress;
                        lifetime.Dispose();
                        return null;
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Waiting to acquire collection capacity for dispatch {DispatchId}.", dispatchId);
                }
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        await lifetime.CancelAsync();
        await progress;
        lifetime.Dispose();
        throw new OperationCanceledException(stoppingToken);
    }

    public sealed class Permit : IAsyncDisposable
    {
        private readonly IMessageBus bus;
        private readonly CollectionCapacityRequest request;
        private readonly CancellationTokenSource lifetime;
        private readonly Task progress;
        private readonly Task renew;
        private readonly ILogger logger;

        internal Permit(IMessageBus bus, CollectionCapacityRequest request,
            CancellationTokenSource lifetime, Task progress, ILogger logger)
        {
            this.bus = bus;
            this.request = request;
            this.lifetime = lifetime;
            this.progress = progress;
            this.logger = logger;
            renew = RenewAsync();
        }

        public CancellationToken CancellationToken => lifetime.Token;

        private async Task RenewAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(RenewInterval, lifetime.Token);
                    var response = await bus.RequestAsync<CollectionCapacityRequest, CollectionCapacityResponse>(
                        WorkerRegistrySubjects.RenewCollection, request, RequestTimeout, lifetime.Token);
                    if (response?.Granted != true)
                    {
                        logger.LogWarning("Collection capacity lease lost for dispatch {DispatchId}.", request.DispatchId);
                        await lifetime.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Collection capacity renewal failed for dispatch {DispatchId}.", request.DispatchId);
                await lifetime.CancelAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(progress, renew);
            try
            {
                await bus.RequestAsync<CollectionCapacityRequest, CollectionCapacityResponse>(
                    WorkerRegistrySubjects.ReleaseCollection, request, RequestTimeout, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Collection capacity release failed for dispatch {DispatchId}; it will expire.", request.DispatchId);
            }
            lifetime.Dispose();
        }
    }
}
