using NodaTime;

namespace Shared.Messaging;

public static class CookieProfileSubjects
{
    public const string Upsert = "fs.cookies.profiles.upsert";
    public const string List = "fs.cookies.profiles.list";
    public const string Get = "fs.cookies.profiles.get";
    public const string Delete = "fs.cookies.profiles.delete";
}

/// <summary>
/// Non-secret cookie profile metadata. The cookie body is never carried in these messages — it is
/// written to / read from OpenBAO directly by the services that need it.
/// </summary>
public sealed record CookieProfileDto
{
    public required Guid Id { get; init; }
    public required string OwnerSubject { get; init; }
    public required string ProfileKey { get; init; }
    public string? Site { get; init; }
    public string? DisplayName { get; init; }
    public required Instant CreatedAt { get; init; }
    public Instant? LastUpdated { get; init; }
}

public sealed record CookieProfileUpsertRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string ProfileKey { get; init; }
    public string? Site { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record CookieProfileListRequestMessage
{
    public required string OwnerSubject { get; init; }
}

public sealed record CookieProfileGetRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string ProfileKey { get; init; }
}

public sealed record CookieProfileDeleteRequestMessage
{
    public required string OwnerSubject { get; init; }
    public required string ProfileKey { get; init; }
}

public sealed record CookieProfileOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public CookieProfileDto? Entity { get; init; }
    public IReadOnlyList<CookieProfileDto>? Items { get; init; }
}
