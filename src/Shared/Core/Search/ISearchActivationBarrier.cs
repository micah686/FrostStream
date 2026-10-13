namespace DataBridge.Search;

/// <summary>Holds database mutations only for a bounded final replay and generation activation.</summary>
public interface ISearchActivationBarrier
{
    Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct);
}
