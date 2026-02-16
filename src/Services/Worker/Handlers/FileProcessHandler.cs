using FlySwattr.NATS.Abstractions;
using Microsoft.Extensions.Logging;
using Shared.Messages;

namespace Worker.Handlers;

/// <summary>
/// Handles file download requests from the JetStream file-processors consumer.
/// </summary>
public static class FileProcessHandler
{
    public static async Task HandleAsync(IJsMessageContext<FileDownloadRequest> context)
    {
        var request = context.Message;

        // TODO: Add actual file processing logic here

        await context.AckAsync();
    }
}
