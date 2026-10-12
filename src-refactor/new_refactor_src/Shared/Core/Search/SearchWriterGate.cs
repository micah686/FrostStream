namespace DataBridge.Search;
/// <summary>Serializes the existing rebuild and revision worker within one control plane.</summary>
public sealed class SearchWriterGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
