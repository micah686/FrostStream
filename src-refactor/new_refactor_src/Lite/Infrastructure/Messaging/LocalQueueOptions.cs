namespace DataBridge.Messaging;

public sealed class LocalQueueOptions
{
    public int MaxConcurrentHandlers { get; set; } = 32;
    public int MaxConsumerConcurrency { get; set; } = 16;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan MaxIdleDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromDays(1);
    public TimeSpan DedupRetention { get; set; } = TimeSpan.FromDays(30);
    public void Validate()
    {
        if (MaxConcurrentHandlers is < 1 or > 128 || MaxConsumerConcurrency is < 1 or > 32 || MaxAttempts is < 1 or > 100 || MaxIdleDelay < TimeSpan.FromMilliseconds(25) || MaxIdleDelay > TimeSpan.FromSeconds(30)
            || CompletedRetention < TimeSpan.Zero || DedupRetention < TimeSpan.FromDays(7) || DedupRetention < CompletedRetention)
            throw new InvalidOperationException("Invalid local queue limits; deduplication must cover at least seven days and completed retention.");
    }
}
public sealed record LocalQueueFailure(string Consumer, long Sequence, string Error, long FailedAt);
