namespace Shared.Deployment;

/// <summary>Local durable work and background producers wait until request/event handlers are installed.</summary>
public sealed class ApplicationStartupGate
{
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WaitAsync(CancellationToken cancellationToken) => initialized.Task.WaitAsync(cancellationToken);
    public void Release() => initialized.TrySetResult();
}
