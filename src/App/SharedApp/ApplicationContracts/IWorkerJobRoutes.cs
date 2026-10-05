namespace FrostStream.ApplicationContracts;

/// <summary>Ensures a durable worker route exists for a configured worker tag.</summary>
public interface IWorkerJobRoutes
{
    Task EnsureTaggedConsumerAsync(string stream, string consumer, string subject, string tag, CancellationToken cancellationToken = default);
}
