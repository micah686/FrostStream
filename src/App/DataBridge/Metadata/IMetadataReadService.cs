using Shared.Messaging;

namespace DataBridge.Metadata;

public interface IMetadataReadService
{
    Task<MetadataDetailDto?> GetDetailAsync(Guid mediaGuid, CancellationToken ct = default);

    Task<MetadataTechnicalDto?> GetTechnicalAsync(Guid mediaGuid, CancellationToken ct = default);

    /// <summary>Picks a uniformly random archived media guid, optionally excluding one item.</summary>
    Task<Guid?> GetRandomMediaGuidAsync(Guid? excludeMediaGuid, CancellationToken ct = default);

    Task<IReadOnlyList<MetadataVersionDto>> ListVersionsAsync(Guid mediaGuid, CancellationToken ct = default);

    Task<AccountsListResult> ListAccountsAsync(int pageSize, string? after, string? platform, CancellationToken ct = default);

    Task<AccountDto?> GetAccountAsync(long accountId, CancellationToken ct = default);

    Task<TaxonomyListResult> ListTaxonomyAsync(MetadataTaxonomyKind kind, int pageSize, int pageOffset, string? search, CancellationToken ct = default);
}

public sealed record AccountsListResult(
    IReadOnlyList<AccountSummaryDto> Items,
    string? NextCursor,
    bool HasMore);

public sealed record TaxonomyListResult(
    IReadOnlyList<TaxonomyItemDto> Items,
    int Total);

public enum MetadataTaxonomyKind
{
    Tags,
    Categories,
    Genres
}

public sealed class InvalidMetadataCursorException(string message) : Exception(message);
