using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public interface IAudioRenditionRepository
{
    Task<AudioRenditionDto?> ResolveAsync(
        Guid mediaGuid,
        AudioRenditionFormat format,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default);

    Task<AudioRenditionDto?> CreateIfMissingAsync(
        Guid mediaGuid,
        AudioRenditionFormat format,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default);

    Task<AudioRenditionWorkItem?> ClaimAsync(Guid renditionId, CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid renditionId,
        string storagePath,
        string contentHashXxh128,
        long sizeBytes,
        int? durationSeconds,
        CancellationToken cancellationToken = default);

    Task<bool> FailAsync(Guid renditionId, string errorMessage, CancellationToken cancellationToken = default);
}
