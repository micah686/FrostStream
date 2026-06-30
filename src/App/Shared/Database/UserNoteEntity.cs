using NodaTime;

namespace Shared.Database;

public class UserNoteEntity
{
    public long Id { get; set; }

    public required string OwnerSubject { get; set; }

    public required string TargetType { get; set; }

    public required string TargetId { get; set; }

    public required string Note { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}
