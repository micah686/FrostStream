using Polly;
using Polly.Retry;

namespace Shared.Resilience;

/// <summary>
/// Factory for creating standardized resilience pipelines across services.
/// </summary>
public static class ResiliencePipelineFactory
{
    /// <summary>
    /// Creates a standard resilience pipeline for storage and network operations.
    /// Retries on IO, timeout, and HTTP exceptions with exponential backoff.
    /// </summary>
    /// <param name="maxRetryAttempts">Maximum number of retry attempts (default: 3)</param>
    /// <param name="delay">Initial delay between retries (default: 2 seconds)</param>
    /// <param name="timeout">Overall timeout for the operation (default: 10 minutes)</param>
    public static ResiliencePipeline CreateStoragePipeline(
        int maxRetryAttempts = 3,
        TimeSpan? delay = null,
        TimeSpan? timeout = null)
    {
        delay ??= TimeSpan.FromSeconds(2);
        timeout ??= TimeSpan.FromMinutes(10);

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = delay.Value,
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<HttpRequestException>()
            })
            .AddTimeout(timeout.Value)
            .Build();
    }

    /// <summary>
    /// Creates a lightweight resilience pipeline for quick operations.
    /// Uses fewer retries and shorter timeouts.
    /// </summary>
    /// <param name="maxRetryAttempts">Maximum number of retry attempts (default: 2)</param>
    /// <param name="delay">Initial delay between retries (default: 5 seconds)</param>
    /// <param name="timeout">Overall timeout for the operation (default: 2 minutes)</param>
    public static ResiliencePipeline CreateLightweightPipeline(
        int maxRetryAttempts = 2,
        TimeSpan? delay = null,
        TimeSpan? timeout = null)
    {
        delay ??= TimeSpan.FromSeconds(5);
        timeout ??= TimeSpan.FromMinutes(2);

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = delay.Value,
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<TimeoutException>()
                    .Handle<HttpRequestException>()
            })
            .AddTimeout(timeout.Value)
            .Build();
    }
}
