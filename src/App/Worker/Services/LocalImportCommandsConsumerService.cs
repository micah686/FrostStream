using System.Buffers;
using System.IO.Hashing;
using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Shared.Imports;
using Shared.Messaging;

namespace Worker.Services;

public sealed class LocalImportCommandsConsumerService(
    IJetStreamConsumer consumer,
    IJetStreamPublisher publisher,
    ITopologyManager topologyManager,
    IClock clock,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LocalImportCommandsConsumerService> logger) : BackgroundService
{
    private static readonly StreamName Stream = StreamName.From(LocalImportTopology.StreamNameValue);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        foreach (var tag in options.Tags)
        {
            await topologyManager.EnsureConsumerAsync(
                LocalImportTopology.TaggedWorkerConsumerSpec(
                    LocalImportTopology.WorkerPrepareLocalImportFileConsumer,
                    LocalImportSubjects.PrepareLocalImportFileCommand,
                    tag),
                stoppingToken);
            logger.LogInformation("Ensured tagged local import prepare consumer for tag '{Tag}'.", tag);
        }

        var consumerTasks = new List<Task>();
        if (options.AcceptsUntaggedJobs || options.Tags.Count == 0)
        {
            consumerTasks.Add(Consume<PrepareLocalImportFileCommand>(
                LocalImportTopology.WorkerPrepareLocalImportFileConsumer,
                HandlePrepareLocalImportFileAsync,
                stoppingToken));
        }

        foreach (var tag in options.Tags)
        {
            consumerTasks.Add(Consume<PrepareLocalImportFileCommand>(
                $"{LocalImportTopology.WorkerPrepareLocalImportFileConsumer}-{tag}",
                HandlePrepareLocalImportFileAsync,
                stoppingToken));
        }

        logger.LogInformation(
            "Subscribed to {Count} local import command consumer(s) on stream {Stream}.",
            consumerTasks.Count,
            Stream.Value);

        await Task.WhenAll(consumerTasks);
    }

    private Task Consume<TCommand>(
        string consumerName,
        Func<IJsMessageContext<TCommand>, Task> handler,
        CancellationToken stoppingToken)
        where TCommand : class, IFlowMessage
        => consumer.ConsumePullAsync(
            stream: Stream,
            consumer: ConsumerName.From(consumerName),
            handler: handler,
            options: null,
            cancellationToken: stoppingToken);

    private async Task HandlePrepareLocalImportFileAsync(IJsMessageContext<PrepareLocalImportFileCommand> context)
    {
        var cmd = context.Message;
        try
        {
            logger.LogInformation(
                "Preparing local import file for BatchId {BatchId} ItemId {ItemId} File {File}.",
                cmd.BatchId,
                cmd.ItemId,
                cmd.File);

            var sourceFile = await PrepareFileAsync(cmd.SourceRoot, cmd.File, workerOptions.Value.AllowedImportRoots);
            var infoJson = cmd.Sidecars?.InfoJson is { } infoJsonPath
                ? await PrepareSidecarAsync(cmd.SourceRoot, infoJsonPath, workerOptions.Value.AllowedImportRoots)
                : null;
            var thumbnail = cmd.Sidecars?.Thumbnail is { } thumbnailPath
                ? await PrepareSidecarAsync(cmd.SourceRoot, thumbnailPath, workerOptions.Value.AllowedImportRoots)
                : null;

            var captions = new List<LocalImportPreparedCaptionSidecar>();
            if (cmd.Sidecars?.Captions is { Count: > 0 } captionSpecs)
            {
                foreach (var caption in captionSpecs)
                {
                    var preparedCaption = await PrepareSidecarAsync(cmd.SourceRoot, caption.File, workerOptions.Value.AllowedImportRoots);
                    captions.Add(new LocalImportPreparedCaptionSidecar
                    {
                        SourceFileRef = preparedCaption.SourceFileRef,
                        FileName = preparedCaption.FileName,
                        SizeBytes = preparedCaption.SizeBytes,
                        ContentHashXxh128 = preparedCaption.ContentHashXxh128,
                        LanguageCode = caption.LanguageCode,
                        CaptionType = caption.CaptionType,
                        Name = caption.Name
                    });
                }
            }

            await Publish(LocalImportSubjects.LocalImportFilePrepared, new LocalImportFilePrepared
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/result"),
                OperationKey = $"{cmd.OperationKey}/result",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                BatchId = cmd.BatchId,
                ItemId = cmd.ItemId,
                SourceFileRef = sourceFile.SourceFileRef,
                FileName = sourceFile.FileName,
                FileSizeBytes = sourceFile.SizeBytes,
                ContentHashXxh128 = sourceFile.ContentHashXxh128,
                InfoJson = infoJson,
                Thumbnail = thumbnail,
                Captions = captions
            });

            await context.AckAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Preparing local import file failed for BatchId {BatchId} ItemId {ItemId} File {File}.",
                cmd.BatchId,
                cmd.ItemId,
                cmd.File);

            await Publish(LocalImportSubjects.LocalImportFilePrepareFailed, new LocalImportFilePrepareFailed
            {
                JobId = cmd.JobId,
                CorrelationId = cmd.CorrelationId,
                CausationId = cmd.MessageId,
                MessageId = DeterministicGuid.Create(cmd.MessageId, "/failed"),
                OperationKey = $"{cmd.OperationKey}/failed",
                OccurredAt = clock.GetCurrentInstant(),
                Attempt = cmd.Attempt,
                BatchId = cmd.BatchId,
                ItemId = cmd.ItemId,
                FailureKind = FailureKind.Permanent,
                ErrorCode = ErrorCode(ex),
                ErrorMessage = ex.Message
            });
            await context.AckAsync();
        }
    }

    private static async Task<LocalImportPreparedSidecar> PrepareFileAsync(
        string sourceRoot,
        string relativePath,
        IReadOnlyList<string> allowedRoots)
    {
        if (!LocalImportPathRules.TryResolveUnderAllowedRoots(
                sourceRoot,
                relativePath,
                allowedRoots,
                out var fullPath,
                out _,
                out var error))
        {
            throw new ArgumentException(error, nameof(relativePath));
        }

        var file = new FileInfo(fullPath);
        if (Directory.Exists(fullPath))
            throw new IOException("Local import path is a directory.");
        if (!file.Exists)
            throw new FileNotFoundException("Local import file was not found.", fullPath);

        return new LocalImportPreparedSidecar
        {
            SourceFileRef = file.FullName,
            FileName = file.Name,
            SizeBytes = file.Length,
            ContentHashXxh128 = await ComputeXxHash128Async(file.FullName)
        };
    }

    private static Task<LocalImportPreparedSidecar> PrepareSidecarAsync(
        string sourceRoot,
        string relativePath,
        IReadOnlyList<string> allowedRoots)
        => PrepareFileAsync(sourceRoot, relativePath, allowedRoots);

    private Task Publish<T>(string subject, T message) where T : IFlowMessage
        => publisher.PublishAsync(subject, message, messageId: message.MessageId.ToString("N"));

    private static async Task<string> ComputeXxHash128Async(string path)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);

        try
        {
            await using var stream = File.OpenRead(path);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
            }

            Span<byte> hash = stackalloc byte[16];
            hasher.GetCurrentHash(hash);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ErrorCode(Exception ex)
        => ex switch
        {
            FileNotFoundException => "source_missing",
            DirectoryNotFoundException => "source_missing",
            ArgumentException => "invalid_source_path",
            UnauthorizedAccessException => "source_access_denied",
            IOException => "invalid_source_file",
            _ => "prepare_failed"
        };
}
