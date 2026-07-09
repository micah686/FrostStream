using System.Diagnostics;
using System.Text.Json;
using Conduit.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Imports;
using Shared.Messaging;

namespace Worker.Services;

public sealed class LocalImportProbeConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    ITopologyManager topologyManager,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LocalImportProbeConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(LocalImportTopology.StreamNameValue);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        foreach (var tag in options.Tags)
        {
            await topologyManager.EnsureConsumerAsync(
                LocalImportTopology.TaggedWorkerConsumerSpec(
                    LocalImportTopology.WorkerProbeImportSessionItemsConsumer,
                    LocalImportSubjects.ProbeImportSessionItemsCommand,
                    tag),
                stoppingToken);
        }

        var tasks = new List<Task>();
        if (options.AcceptsUntaggedJobs || options.Tags.Count == 0)
        {
            tasks.Add(Consume(LocalImportTopology.WorkerProbeImportSessionItemsConsumer, stoppingToken));
        }

        foreach (var tag in options.Tags)
        {
            tasks.Add(Consume($"{LocalImportTopology.WorkerProbeImportSessionItemsConsumer}-{tag}", stoppingToken));
        }

        logger.LogInformation("Subscribed to {Count} local import probe consumer(s).", tasks.Count);
        await Task.WhenAll(tasks);
    }

    private Task Consume(string consumerName, CancellationToken stoppingToken)
        => consumer.ConsumePullAsync<ProbeImportSessionItemsCommand>(
            Stream,
            ConsumerName.From(consumerName),
            HandleAsync,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandleAsync(IJsMessageContext<ProbeImportSessionItemsCommand> context)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var heartbeatTask = JetStreamHeartbeat.RunAsync(context, HeartbeatInterval, logger, "Local import probe", heartbeatCts.Token);
        try
        {
            await ProbeAsync(context.Message);
            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed probing local import session {SessionId}; nacking.", context.Message.SessionId);
            await context.NackAsync();
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { }
        }
    }

    private async Task ProbeAsync(ProbeImportSessionItemsCommand command)
    {
        var successes = new List<ImportSessionProbeResult>();
        var failures = new List<ImportSessionProbeFailure>();

        foreach (var item in command.Items)
        {
            try
            {
                var path = ResolveIncomingPath(item.RelativePath);
                var probe = await RunFfprobeAsync(path);
                successes.Add(new ImportSessionProbeResult
                {
                    ItemId = item.ItemId,
                    ProbeMetadataJson = probe.Json,
                    DurationSeconds = probe.DurationSeconds,
                    Width = probe.Width,
                    Height = probe.Height
                });
            }
            catch (Exception ex)
            {
                failures.Add(new ImportSessionProbeFailure
                {
                    ItemId = item.ItemId,
                    ErrorCode = ErrorCode(ex),
                    ErrorMessage = ex.Message
                });
            }
        }

        if (successes.Count > 0 || failures.Count > 0)
        {
            var messageId = DeterministicGuid.Create(command.MessageId, "/probed");
            await publisher.PublishAsync(
                LocalImportSubjects.ImportSessionItemsProbed,
                new ImportSessionItemsProbed
                {
                    JobId = command.JobId,
                    CorrelationId = command.CorrelationId,
                    CausationId = command.MessageId,
                    MessageId = messageId,
                    OperationKey = $"{command.OperationKey}/probed",
                    OccurredAt = clock.GetCurrentInstant(),
                    Attempt = command.Attempt,
                    SessionId = command.SessionId,
                    Results = successes,
                    Failures = failures
                },
                messageId: messageId.ToString("N"));
        }
    }

    private string ResolveIncomingPath(string relativePath)
    {
        var incomingRoot = workerOptions.Value.IncomingRoot;
        if (!LocalImportPathRules.TryResolveUnderAllowedRoots(
                incomingRoot,
                relativePath,
                [incomingRoot],
                out var fullPath,
                out _,
                out var error))
        {
            throw new ArgumentException(error, nameof(relativePath));
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Local import file was not found.", fullPath);

        return fullPath;
    }

    private static async Task<ProbeOutput> RunFfprobeAsync(string path)
    {
        var ffprobePath = Path.Combine(AppContext.BaseDirectory, "tools", YtDlpPaths.FfprobeFileName);
        if (!File.Exists(ffprobePath))
            ffprobePath = YtDlpPaths.FfprobeFileName;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-print_format");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("-show_format");
        process.StartInfo.ArgumentList.Add("-show_streams");
        process.StartInfo.ArgumentList.Add(path);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start ffprobe.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ffprobe timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffprobe failed." : stderr.Trim());
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("ffprobe returned an empty response.");

        var (duration, width, height) = ParseProbe(stdout);
        return new ProbeOutput(stdout, duration, width, height);
    }

    private static (double? DurationSeconds, int? Width, int? Height) ParseProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            double? duration = null;
            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.String
                && double.TryParse(durationElement.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedDuration))
            {
                duration = parsedDuration;
            }

            int? width = null;
            int? height = null;
            if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var codecType)
                        && codecType.GetString() == "video")
                    {
                        if (stream.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var parsedWidth))
                            width = parsedWidth;
                        if (stream.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var parsedHeight))
                            height = parsedHeight;
                        break;
                    }
                }
            }

            return (duration, width, height);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string ErrorCode(Exception ex)
        => ex switch
        {
            FileNotFoundException => "source_missing",
            ArgumentException => "invalid_source_path",
            TimeoutException => "probe_timeout",
            _ => "probe_failed"
        };

    private sealed record ProbeOutput(string Json, double? DurationSeconds, int? Width, int? Height);
}
