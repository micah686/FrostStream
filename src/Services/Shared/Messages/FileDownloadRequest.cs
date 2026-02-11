namespace Shared.Messages;

/// <summary>
/// Request to process a file, sent from WebAPI to Workers.
/// </summary>
/// <param name="Filename">The name of the file to process.</param>
/// <param name="StorageKey">The storage key identifying the file's location.</param>
public record FileDownloadRequest(string Filename, string StorageKey);
