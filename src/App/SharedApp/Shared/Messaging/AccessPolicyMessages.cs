using NodaTime;

namespace Shared.Messaging;

public static class AccessPolicySubjects
{
    public const string List = "access-policy.list";
    public const string Get = "access-policy.get";
    public const string Save = "access-policy.save";
    public const string Delete = "access-policy.delete";
    public const string SetSync = "access-policy.set-sync";
    public const string ProviderCatalog = "access-policy.provider-catalog";
    public const string MediaSummary = "access-policy.media-summary";
    public const string EffectiveMedia = "access-policy.effective-media";
    public const string QueueGroup = "databridge-access-policy";
}

public enum AccessPolicySyncStatus
{
    Pending,
    Synced,
    Failed
}

public sealed record AccessPolicyAssignmentDto
{
    public required string Type { get; init; }
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record AccessPolicyDto
{
    public required Guid PolicyId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public AccessPolicySyncStatus SyncStatus { get; init; }
    public string? SyncError { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<string> BundleIds { get; init; } = [];
    public IReadOnlyList<Guid> MediaGuids { get; init; } = [];
    public IReadOnlyList<string> Providers { get; init; } = [];
    public IReadOnlyList<int> AgeThresholds { get; init; } = [];
    public IReadOnlyList<AccessPolicyAssignmentDto> Assignments { get; init; } = [];
    public Instant CreatedAt { get; init; }
    public string? CreatedBySubject { get; init; }
    public Instant UpdatedAt { get; init; }
    public string? UpdatedBySubject { get; init; }
}

public sealed record AccessPolicyListRequestMessage;

public sealed record AccessPolicyGetRequestMessage
{
    public required Guid PolicyId { get; init; }
}

public sealed record AccessPolicySaveRequestMessage
{
    public required AccessPolicyDto Policy { get; init; }
}

public sealed record AccessPolicyDeleteRequestMessage
{
    public required Guid PolicyId { get; init; }
}

public sealed record AccessPolicySetSyncRequestMessage
{
    public required Guid PolicyId { get; init; }
    public required long Version { get; init; }
    public required AccessPolicySyncStatus Status { get; init; }
    public string? Error { get; init; }
}

public sealed record AccessPolicyMediaSummaryRequestMessage
{
    public required Guid MediaGuid { get; init; }
}

public sealed record AccessPolicyMediaSummaryDto
{
    public required Guid MediaGuid { get; init; }
    public bool Found { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string> Providers { get; init; } = [];
    public int? AgeLimit { get; init; }
}

public sealed record AccessPolicyEffectiveMediaRequestMessage
{
    public required Guid MediaGuid { get; init; }
    public string? UserSubject { get; init; }
    public IReadOnlyList<string> UserGroups { get; init; } = [];
}

public sealed record AccessPolicyAxisDecisionDto
{
    public required string Axis { get; init; }
    public required bool Restricted { get; init; }
    public required bool Allowed { get; init; }
    public string? Resource { get; init; }
    public IReadOnlyList<Guid> MatchingPolicyIds { get; init; } = [];
    public IReadOnlyList<Guid> GrantingPolicyIds { get; init; } = [];
    public IReadOnlyList<Guid> DenyingPolicyIds { get; init; } = [];
    public required string Reason { get; init; }
}

public sealed record AccessPolicyEffectiveMediaDto
{
    public required Guid MediaGuid { get; init; }
    public bool Found { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string> Providers { get; init; } = [];
    public int? AgeLimit { get; init; }
    public bool IsAllowed { get; init; }
    public IReadOnlyList<AccessPolicyAxisDecisionDto> Decisions { get; init; } = [];
}

public sealed record AccessPolicyOperationResponseMessage
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public AccessPolicyDto? Policy { get; init; }
    public IReadOnlyList<AccessPolicyDto>? Policies { get; init; }
    public IReadOnlyList<string>? Providers { get; init; }
    public AccessPolicyMediaSummaryDto? MediaSummary { get; init; }
    public AccessPolicyEffectiveMediaDto? EffectiveMedia { get; init; }
}
