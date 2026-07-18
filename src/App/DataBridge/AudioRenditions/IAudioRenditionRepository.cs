using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public interface IAudioRenditionRepository
{
    Task<ChannelAudioResolveResult?> ResolveChannelAsync(
        long accountId,
        bool createIfMissing,
        bool retryFailedAndPending,
        CancellationToken cancellationToken = default);

    Task<AudioRenditionDto?> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? sourceVersion,
        CancellationToken cancellationToken = default);

    Task<AudioRenditionDto?> CreateIfMissingAsync(
        Guid mediaGuid,
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

public sealed record ChannelAudioResolveResult(
    ChannelAudioDto Channel,
    IReadOnlyList<AudioRenditionDto> RenditionsToQueue);
