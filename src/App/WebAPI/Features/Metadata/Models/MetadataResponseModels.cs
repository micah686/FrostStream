using Shared.Messaging;

namespace WebAPI.Features.Metadata.Models;

public sealed record PagedMetadataResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int TotalCount,
    bool HasMore);

public sealed record MetadataListResponse<T>(
    IReadOnlyList<T> Items,
    int TotalCount);

public sealed record AccountListResponse(
    IReadOnlyList<AccountSummaryDto> Items,
    string? NextCursor,
    bool HasMore);

public sealed record TaxonomyListResponse(
    IReadOnlyList<TaxonomyItemDto> Items,
    int Total);

public sealed record MetadataVersionsResponse(
    int TotalCount,
    IReadOnlyList<MetadataVersionDto> Versions);
