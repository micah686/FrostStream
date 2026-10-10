using FrostStream.ApplicationContracts;
using Microsoft.Extensions.Hosting;

namespace Shared.Messaging;

/// <summary>Exposes asynchronous initialization separately from a long-running background loop.</summary>
public abstract class InitializingBackgroundService : BackgroundService, IApplicationHandlerInitialization
{
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task RegistrationCompleted => initialized.Task;
    protected void MarkInitialized() => initialized.TrySetResult();

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteInitializedAsync(stoppingToken);
            if (!initialized.Task.IsCompleted)
                initialized.TrySetException(new InvalidOperationException("The background service exited before initialization completed."));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            initialized.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (Exception ex)
        {
            initialized.TrySetException(ex);
            throw;
        }
    }

    protected abstract Task ExecuteInitializedAsync(CancellationToken stoppingToken);
}
