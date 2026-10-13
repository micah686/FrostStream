using NodaTime;
namespace DataBridge.Messaging;
public sealed class DownloadFlowStartupState
{
    public Instant GenerationStartedAt { get; private set; } = Instant.MaxValue;
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    public void BeginStartup() { Volatile.Write(ref ready, 0); GenerationStartedAt = Instant.MaxValue; }
    public void MarkReady(Instant generation) { GenerationStartedAt = generation; Volatile.Write(ref ready, 1); }
}

