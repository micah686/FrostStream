using Shared.Messaging;

namespace DataBridge.StreamRenditions;

public interface IStreamRenditionRepository
{
    Task<StreamRenditionDto?> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default);

    Task<StreamRenditionDto?> CreateIfMissingAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default);

    Task<StreamRenditionWorkItem?> ClaimAsync(Guid renditionId, CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid renditionId,
        string storagePath,
        long sizeBytes,
        int? durationSeconds,
        CancellationToken cancellationToken = default);

    Task<bool> FailAsync(Guid renditionId, string errorMessage, CancellationToken cancellationToken = default);
}
