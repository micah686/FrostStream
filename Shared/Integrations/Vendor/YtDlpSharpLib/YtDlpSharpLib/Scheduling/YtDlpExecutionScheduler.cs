using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Progress;

namespace YtDlpSharpLib.Scheduling;

/// <summary>
/// Default <see cref="IYtDlpExecutionScheduler"/>. Throttles concurrent downloads with a
/// <see cref="SemaphoreSlim"/> sized from <see cref="YtDlpClientOptions.DownloadConcurrency"/>,
/// or from a per-call override.
/// </summary>
public sealed class YtDlpExecutionScheduler : IYtDlpExecutionScheduler, IDisposable
{
    private readonly IYtDlpClient _client;
    private readonly int _defaultConcurrency;
    private readonly SemaphoreSlim _semaphore;

    /// <summary>Creates a scheduler from typed options. Suitable for direct (non-DI) usage.</summary>
    public YtDlpExecutionScheduler(IYtDlpClient client, YtDlpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _defaultConcurrency = Math.Max(1, options.DownloadConcurrency);
        _semaphore = new SemaphoreSlim(_defaultConcurrency, _defaultConcurrency);
    }

    /// <summary>Creates a scheduler from <see cref="IOptions{TOptions}"/>. Used by the DI container.</summary>
    public YtDlpExecutionScheduler(IYtDlpClient client, IOptions<YtDlpClientOptions> options)
        : this(client, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    /// <inheritdoc />
    public async Task DownloadAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _client.DownloadAsync(url, outputDirectory, options, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DownloadResult> ExecuteBulkAsync(
        IEnumerable<DownloadRequest> requests,
        CancellationToken ct = default) =>
        ExecuteAsync(requests, maxConcurrency: null, ct);

    /// <inheritdoc />
    public async IAsyncEnumerable<DownloadResult> ExecuteAsync(
        IEnumerable<DownloadRequest> requests,
        int? maxConcurrency = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        SemaphoreSlim? localSemaphore = null;
        SemaphoreSlim semaphore;

        if (maxConcurrency is { } limit)
        {
            var sized = Math.Max(1, limit);
            localSemaphore = new SemaphoreSlim(sized, sized);
            semaphore = localSemaphore;
        }
        else
        {
            semaphore = _semaphore;
        }

        try
        {
            var pending = new List<Task<DownloadResult>>();
            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                pending.Add(RunWithSemaphoreAsync(request, semaphore, ct));
            }

            await foreach (var completed in Task.WhenEach(pending).WithCancellation(ct).ConfigureAwait(false))
            {
                yield return await completed.ConfigureAwait(false);
            }
        }
        finally
        {
            localSemaphore?.Dispose();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-job exceptions are surfaced via DownloadResult.Error per the scheduler contract.")]
    private async Task<DownloadResult> RunWithSemaphoreAsync(
        DownloadRequest request,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DispatchAsync(request, ct).ConfigureAwait(false);
            return DownloadResult.Succeeded(request);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var exitCode = (ex as YtDlpException)?.ExitCode;
            var errorOutput = ex is YtDlpProcessException processException
                              && !string.IsNullOrWhiteSpace(processException.LastStderrLines)
                ? processException.LastStderrLines
                : ex.Message;
            return DownloadResult.Failed(request, errorOutput, ex, exitCode);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private Task DispatchAsync(DownloadRequest request, CancellationToken ct) =>
        request.Kind switch
        {
            DownloadRequestKind.Video => _client.DownloadAsync(
                request.Url,
                request.OutputDirectory,
                request.DownloadOptions,
                request.Progress,
                ct),
            DownloadRequestKind.Audio => _client.DownloadAudioAsync(
                request.Url,
                request.OutputDirectory,
                request.AudioOptions,
                request.Progress,
                ct),
            DownloadRequestKind.Playlist => _client.DownloadPlaylistAsync(
                request.Url,
                request.OutputDirectory,
                request.PlaylistOptions,
                request.Progress,
                ct),
            DownloadRequestKind.AudioPlaylist => _client.DownloadAudioPlaylistAsync(
                request.Url,
                request.OutputDirectory,
                request.AudioPlaylistOptions,
                request.Progress,
                ct),
            DownloadRequestKind.Metadata => _client.DownloadMetadataAsync(
                request.Url,
                request.OutputDirectory,
                request.MetadataOptions,
                ct),
            DownloadRequestKind.LiveChat => _client.DownloadLiveChatAsync(
                request.Url,
                request.OutputDirectory,
                request.LiveChatOptions,
                ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown download request kind.")
        };

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}
