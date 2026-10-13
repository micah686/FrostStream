namespace Shared.Messaging;

public static class UserSessionSubjects
{
    /// <summary>Request/reply: upsert the local FrostStream user from a validated session.</summary>
    public const string Upsert = "fs.users.session.upsert";
}

/// <summary>
/// Sent by WebAPI after it validates an access token (BFF session sync). DataBridge upserts the
/// local <c>auth.froststream_users</c> row keyed by the Authentik subject. Group names are carried
/// for audit/diagnostics; OpenFGA tuple sync happens in WebAPI, not DataBridge.
/// </summary>
public sealed record UserSessionUpsertRequestMessage
{
    public required string Subject { get; init; }

    public required string DisplayName { get; init; }

    public string? Email { get; init; }

    public IReadOnlyList<string> Groups { get; init; } = [];
}

public sealed record UserSessionUpsertResponseMessage
{
    public bool Success { get; init; }

    public Guid UserId { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
