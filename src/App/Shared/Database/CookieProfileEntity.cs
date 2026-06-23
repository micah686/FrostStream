using NodaTime;

namespace Shared.Database;

/// <summary>
/// Non-secret metadata for a user-owned cookie profile. The cookie body itself is never stored
/// here — it lives in OpenBAO at <c>cookies/users/{OwnerSubject}/{ProfileKey}</c>.
/// </summary>
public sealed class CookieProfileEntity
{
    public Guid Id { get; set; }

    public required string OwnerSubject { get; set; }

    public required string ProfileKey { get; set; }

    public string? Site { get; set; }

    public string? DisplayName { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }
}
