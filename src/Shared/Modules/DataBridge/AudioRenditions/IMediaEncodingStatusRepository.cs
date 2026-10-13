using Shared.Messaging;

namespace DataBridge.AudioRenditions;

public interface IMediaEncodingStatusRepository
{
    /// <summary>
    /// Sets the durable encoded status for one media item. Returns null if <paramref name="mediaGuid"/>
    /// does not belong to <paramref name="accountId"/>.
    /// </summary>
    Task<MediaEncodingStatusDto?> SetAsync(
        long accountId,
        Guid mediaGuid,
        bool isEncoded,
        string? storageKey,
        string? storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="SetAsync"/>, but for callers (e.g. the watch endpoint) that only know the
    /// media guid, not the owning channel. Resolves the account internally; returns null if the media
    /// has no channel/account association at all.
    /// </summary>
    Task<MediaEncodingStatusDto?> SetByMediaGuidAsync(
        Guid mediaGuid,
        bool isEncoded,
        string? storageKey,
        string? storagePath,
        CancellationToken cancellationToken = default);

    Task<ChannelEncodingStatusPage> ListChannelAsync(
        long accountId,
        bool? isEncodedFilter,
        string? storageKey,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default);
}

public sealed record ChannelEncodingStatusPage(
    IReadOnlyList<ChannelEncodedMediaItemDto> Items,
    string? NextCursor,
    int TotalCount,
    int EncodedCount);
