using NodaTime;

namespace Shared.Database;

public sealed class FrostStreamUserEntity
{
    public Guid Id { get; set; }

    public required string AuthentikSubjectId { get; set; }

    public required string DisplayName { get; set; }

    public Instant? LastSeenAt { get; set; }

    public string? Preferences { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant? LastUpdated { get; set; }
}
