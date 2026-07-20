using Conduit.NATS;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Imports;
using Shared.Messaging;

namespace Worker.Services;

public sealed class LocalImportBrowseConsumerService(
    IMessageBus messageBus,
    IOptions<WorkerOptions> workerOptions,
    ILogger<LocalImportBrowseConsumerService> logger) : SubscriptionBackgroundService
{
    private const string QueueGroup = "worker-import-browse";

    protected override async Task RegisterSubscriptionsAsync(CancellationToken stoppingToken)
    {
        var options = workerOptions.Value;
        if (options.AcceptsUntaggedJobs || options.Tags.Count == 0)
        {
            await SubscribeAsync<BrowseImportIncomingRequest>(messageBus, LocalImportSubjects.BrowseIncomingRequest, HandleAsync, QueueGroup, stoppingToken);
            await SubscribeAsync<RefreshImportMetadataRequest>(messageBus, LocalImportSubjects.RefreshMetadataRequest, HandleRefreshAsync, QueueGroup, stoppingToken);
        }

        foreach (var tag in options.Tags)
        {
            await SubscribeAsync<BrowseImportIncomingRequest>(messageBus, LocalImportSubjects.BrowseIncomingRequestForTag(tag), HandleAsync, $"{QueueGroup}-{tag}", stoppingToken);
            await SubscribeAsync<RefreshImportMetadataRequest>(messageBus, LocalImportSubjects.RefreshMetadataRequestForTag(tag), HandleRefreshAsync, $"{QueueGroup}-{tag}", stoppingToken);
        }
    }

    private async Task HandleAsync(IMessageContext<BrowseImportIncomingRequest> context)
    {
        try
        {
            await context.RespondAsync(Browse(context.Message.SubPath));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed browsing import incoming folder {SubPath}.", context.Message.SubPath);
            await context.RespondAsync(new BrowseImportIncomingResponse { Success = false, ErrorCode = "browse_failed", ErrorMessage = ex.Message });
        }
    }

    private async Task HandleRefreshAsync(IMessageContext<RefreshImportMetadataRequest> context)
    {
        try
        {
            await context.RespondAsync(await RefreshAsync(context.Message, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed refreshing local import metadata sidecars.");
            await context.RespondAsync(new RefreshImportMetadataResponse { Success = false, ErrorCode = "metadata_refresh_failed", ErrorMessage = ex.Message });
        }
    }

    private BrowseImportIncomingResponse Browse(string? subPath)
    {
        var incomingRoot = workerOptions.Value.IncomingRoot;
        var fullPath = incomingRoot;
        var normalized = string.Empty;
        if (!string.IsNullOrWhiteSpace(subPath)
            && !LocalImportPathRules.TryResolveUnderAllowedRoots(incomingRoot, subPath, [incomingRoot], out fullPath, out normalized, out var error))
            return new BrowseImportIncomingResponse { Success = false, ErrorCode = "validation", ErrorMessage = error };

        if (!Directory.Exists(fullPath))
            return new BrowseImportIncomingResponse { Success = false, ErrorCode = "not_found", ErrorMessage = "The selected incoming folder was not found on this worker." };

        return new BrowseImportIncomingResponse
        {
            Success = true,
            SubPath = normalized,
            Directories = Directory.EnumerateDirectories(fullPath)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !name.StartsWith('.'))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private async Task<RefreshImportMetadataResponse> RefreshAsync(
        RefreshImportMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var found = new List<RefreshImportMetadataFoundItem>();
        foreach (var item in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveIncomingPath(item.RelativePath, out var sourcePath, out var error))
                return new RefreshImportMetadataResponse { Success = false, ErrorCode = "validation", ErrorMessage = error };

            var infoJsonPath = ResolveInfoJsonPath(sourcePath);
            if (infoJsonPath is null)
                continue;

            var enrichedJson = await File.ReadAllTextAsync(infoJsonPath, cancellationToken);
            using var document = JsonDocument.Parse(enrichedJson);
            var root = document.RootElement;
            found.Add(new RefreshImportMetadataFoundItem
            {
                ItemId = item.ItemId,
                EnrichedMetadataJson = enrichedJson,
                Title = FirstNonBlank(ReadString(root, "title"), ReadString(root, "fulltitle")),
                Provider = FirstNonBlank(ReadString(root, "extractor"), ReadString(root, "extractor_key"), item.Provider),
                SourceMediaId = FirstNonBlank(ReadString(root, "id"), ReadString(root, "display_id")),
                SourceUrl = FirstNonBlank(ReadString(root, "webpage_url"), ReadString(root, "original_url"), item.SourceUrl),
                InfoJsonRelativePath = Path.GetRelativePath(workerOptions.Value.IncomingRoot, infoJsonPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
            });
        }

        return new RefreshImportMetadataResponse
        {
            Success = true,
            CheckedCount = request.Items.Count,
            FoundCount = found.Count,
            Items = found
        };
    }

    private bool TryResolveIncomingPath(string relativePath, out string fullPath, out string? error)
    {
        var incomingRoot = workerOptions.Value.IncomingRoot;
        if (!LocalImportPathRules.TryResolveUnderAllowedRoots(
                incomingRoot,
                relativePath,
                [incomingRoot],
                out fullPath,
                out _,
                out error))
        {
            return false;
        }

        if (File.Exists(fullPath))
            return true;

        error = "Local import file was not found.";
        return false;
    }

    private static string? ResolveInfoJsonPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath));
        foreach (var candidate in new[] { $"{stem}.info.json", $"{stem}.json" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? FirstNonBlank(value.GetString())
            : null;
}
