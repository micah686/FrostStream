using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Progress;

namespace YtDlpSharpLib.Scheduling;

/// <summary>
/// Executes one or more <see cref="DownloadRequest"/>s with a configurable concurrency limit.
/// </summary>
public interface IYtDlpExecutionScheduler
{
    /// <summary>
    /// Submits a single download to be executed under the scheduler's concurrency limit.
    /// </summary>
    Task DownloadAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Submits a batch of downloads, yielding each <see cref="DownloadResult"/> as it completes.
    /// Failures are reported via <see cref="DownloadResult.Error"/> and do not stop other jobs.
    /// </summary>
    /// <param name="requests">The batch of jobs to execute.</param>
    /// <param name="maxConcurrency">
    /// Optional per-call concurrency override. When <see langword="null"/>, the scheduler's
    /// configured concurrency is used.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    IAsyncEnumerable<DownloadResult> ExecuteAsync(
        IEnumerable<DownloadRequest> requests,
        int? maxConcurrency = null,
        CancellationToken ct = default);

    /// <summary>
    /// Backwards-compatible alias for <see cref="ExecuteAsync(IEnumerable{DownloadRequest}, int?, CancellationToken)"/>.
    /// </summary>
    IAsyncEnumerable<DownloadResult> ExecuteBulkAsync(
        IEnumerable<DownloadRequest> requests,
        CancellationToken ct = default);
}
