using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Messages;

namespace Worker.Handlers;

/// <summary>
/// Background service that consumes file processing requests from NATS.
/// Uses queue group for load balancing across multiple worker instances.
/// </summary>
public class FileProcessHandler : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly ILogger<FileProcessHandler> _logger;

    private const string QueueGroup = "file-processors";

    public FileProcessHandler(IMessageBus messageBus, ILogger<FileProcessHandler> logger)
    {
        _messageBus = messageBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FileProcessHandler starting, subscribing to {Subject} with queue group {QueueGroup}",
            Subjects.DownloadFile, QueueGroup);

        await _messageBus.SubscribeAsync<FileDownloadRequest>(
            Subjects.DownloadFile,
            async ctx =>
            {
                var request = ctx.Message;
                _logger.LogInformation(
                    "Received file process request: Filename={Filename}, StorageKey={StorageKey}",
                    request.Filename,
                    request.StorageKey);

                // TODO: Add actual file processing logic here
                await Task.CompletedTask;
            },
            queueGroup: QueueGroup,
            cancellationToken: stoppingToken);

        _logger.LogInformation("FileProcessHandler ready and listening");

        // Keep the service alive
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
