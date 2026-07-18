using NodaTime;
using Shared.Downloads;

namespace Shared.Messaging;

public static class DownloadConfigSetSubjects
{
    // Must stay outside the FROSTSTREAM_DOWNLOAD stream's "download.>" subject filter:
    // JetStream pub-acks on captured subjects win the request/reply race against the consumer.
    public const string Create = "download-config-set.create";
    public const string Update = "download-config-set.update";
    public const string Get = "download-config-set.get";
    public const string List = "download-config-set.list";
    public const string Delete = "download-config-set.delete";
    public const string Resolve = "download-config-set.resolve";
    public const string ProcessorsQueueGroup = "databridge-download-config-sets";
}

public sealed record DownloadConfigSetDto
{
    public required long Id { get; init; }
    public required string OwnerSubject { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? StorageKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public string? YtDlpOptionsJson { get; init; }
    public IReadOnlyList<IgnoreKeyword> IgnoreKeywords { get; init; } = [];
    public int Priority { get; init; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; init; }
}

public record DownloadConfigSetCreateRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? StorageKey { get; init; }
    public string? CookieProfileKey { get; init; }
    public string? YtDlpOptionsJson { get; init; }
    public IReadOnlyList<IgnoreKeyword> IgnoreKeywords { get; init; } = [];
    public int Priority { get; init; }
}

public sealed record DownloadConfigSetUpdateRequestMessage : DownloadConfigSetCreateRequestMessage;

public sealed record DownloadConfigSetGetRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string Key { get; init; }
}

public sealed record DownloadConfigSetListRequestMessage
{
    public required string OwnerSubject { get; init; }
}

public sealed record DownloadConfigSetDeleteRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string Key { get; init; }
}

public sealed record DownloadConfigSetResolveRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string Key { get; init; }
}

public sealed record DownloadConfigSetOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DownloadConfigSetDto? Entity { get; init; }
    public IReadOnlyList<DownloadConfigSetDto>? Items { get; init; }
}
