using NodaTime;
using Shared.Messaging;

namespace Shared.Database;

public sealed class DownloadConfigSetEntity
{
    public long Id { get; set; }

    public required string OwnerSubject { get; set; }

    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public string? StorageKey { get; set; }

    public string? CookieProfileKey { get; set; }

    public string? YtDlpOptionsJson { get; set; }

    public bool EncodeForPlaylist { get; set; }

    public AudioRenditionFormat AudioFormat { get; set; } = AudioRenditionFormat.Aac;

    public int Priority { get; set; }

    public bool FetchComments { get; set; }

    public Instant CreatedAt { get; private set; } = SystemClock.Instance.GetCurrentInstant();

    public Instant UpdatedAt { get; set; } = SystemClock.Instance.GetCurrentInstant();
}
