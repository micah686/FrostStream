using CastMedia = Sharpcaster.Models.Media.Media;

namespace WebAPI.Features.Media.Casting;

/// <summary>A Chromecast-family device discovered on the local network via mDNS.</summary>
public sealed record CastDeviceDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; }
    public string? Model { get; init; }
    public string? Version { get; init; }
    public string? Status { get; init; }
}

public sealed record StartCastSessionRequest
{
    public required Guid MediaGuid { get; init; }
    public bool AudioOnly { get; init; }
    public string Format { get; init; } = AudioRenditionHelpers.DefaultFormat;
    public string? SubtitleLanguage { get; init; }
    public string? CaptionType { get; init; }
    public double? StartPositionSeconds { get; init; }
}

public sealed record CastSeekRequest
{
    public required double Seconds { get; init; }
}

public sealed record CastVolumeRequest
{
    /// <summary>Device volume level in [0, 1]. Omit to leave the level unchanged.</summary>
    public double? Level { get; init; }

    /// <summary>Device mute state. Omit to leave mute unchanged.</summary>
    public bool? Muted { get; init; }
}

/// <summary>
/// Last-known playback state of a cast session. <c>PlayerState</c> carries the receiver's
/// PLAYING/PAUSED/BUFFERING/IDLE/LOADING value (or DISCONNECTED after teardown); clients
/// interpolate <c>CurrentTime</c> from <c>UpdatedAt</c> while playing because the receiver only
/// pushes status on transitions.
/// </summary>
public sealed record CastSessionSnapshot
{
    public required string PlayerState { get; init; }
    public double CurrentTime { get; init; }
    public double? DurationSeconds { get; init; }
    public double? VolumeLevel { get; init; }
    public bool? Muted { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CastSessionDto
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public required CastSessionSnapshot Snapshot { get; init; }
    public DateTimeOffset TokenExpiresAt { get; init; }
}

/// <summary>One item on a cast-session SSE stream: a status frame or the terminal ended frame.</summary>
public sealed record CastSessionEvent(string Name, CastSessionDto Session)
{
    public const string StatusEvent = "status";
    public const string EndedEvent = "ended";
}

/// <summary>Everything the session manager needs to load media on a device, pre-built by the controller.</summary>
public sealed record CastLoadSpec
{
    public required Guid MediaGuid { get; init; }
    public required string Title { get; init; }
    public required CastMedia Media { get; init; }
    public int[]? ActiveTrackIds { get; init; }
    public double? StartPositionSeconds { get; init; }
    public DateTimeOffset TokenExpiresAt { get; init; }
}

/// <summary>Thrown when a device id does not match any discovered receiver.</summary>
public sealed class CastDeviceNotFoundException(string deviceId)
    : Exception($"No cast device with id '{deviceId}' was found on the network.");

/// <summary>Thrown when a transport command targets a device with no active session.</summary>
public sealed class CastSessionNotFoundException(string deviceId)
    : Exception($"No active cast session for device '{deviceId}'.");

/// <summary>Thrown when connecting to or commanding a device fails at the protocol level.</summary>
public sealed class CastDeviceUnreachableException(string message, Exception? inner = null)
    : Exception(message, inner);
