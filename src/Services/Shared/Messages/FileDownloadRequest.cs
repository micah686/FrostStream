namespace Shared.Messages;

/// <summary>
/// Request to process a file, sent from WebAPI to Workers.
/// </summary>
/// <param name="JobId">The unique job identifier.</param>
/// <param name="Url">The URL of the video to download.</param>
/// <param name="StorageKey">The storage key identifying the file's location.</param>
public record FileDownloadRequest(Guid JobId, string Url, string StorageKey);
