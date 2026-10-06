namespace Shared.LiveChat;

/// <summary>
/// Configuration for the optional live-chat replay feature backed by ClickHouse.
/// Disabled by default; the AppHost only injects these values when LIVE_CHAT_ENABLED is set.
/// WebAPI binds the section too but only reads <see cref="Enabled"/> — chat queries proxy to
/// DataBridge over NATS, and only DataBridge talks to ClickHouse.
/// </summary>
public sealed record LiveChatOptions
{
    public const string SectionName = "LiveChat";

    /// <summary>Whether live-chat replay is enabled for this deployment.</summary>
    public bool Enabled { get; init; }

    /// <summary>ClickHouse HTTP endpoint (e.g. <c>http://localhost:24060</c>).</summary>
    public string Url { get; init; } = "http://localhost:24060";

    public string Database { get; init; } = "froststream";

    public string User { get; init; } = "froststream";

    public string Password { get; init; } = "";

    /// <summary>Rows per bulk insert while ingesting a chat sidecar.</summary>
    public int IngestBatchSize { get; init; } = 25_000;

    /// <summary>Hard cap on messages returned by a single window query.</summary>
    public int MaxWindowRows { get; init; } = 1_000;
}
