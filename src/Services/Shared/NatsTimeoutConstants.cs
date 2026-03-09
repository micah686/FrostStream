namespace Shared;

/// <summary>
/// Centralized timeout constants for NATS operations across all services.
/// </summary>
public static class NatsTimeoutConstants
{
    /// <summary>
    /// Default timeout for standard NATS request-reply operations.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Short timeout for lightweight operations like health checks or storage config lookup.
    /// </summary>
    public static readonly TimeSpan ShortRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Extended timeout for operations that may take longer (e.g., commit operations).
    /// </summary>
    public static readonly TimeSpan ExtendedRequestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Interval for sending in-progress heartbeats during long-running operations.
    /// </summary>
    public static readonly TimeSpan InProgressHeartbeatInterval = TimeSpan.FromSeconds(10);
}
