namespace Shared.Messaging;

/// <summary>
/// NATS subjects for media watch-time access control. The <see cref="Check"/> subject is the
/// server-to-server gate invoked by the stream endpoints; the rest back the management API used by
/// admins/moderators to configure per-media, per-provider, and age-limit restrictions.
/// </summary>
public static class MediaAccessSubjects
{
    public const string Check = "media-access.check";

    public const string MediaList = "media-access.media.list";
    public const string MediaAdd = "media-access.media.add";
    public const string MediaRemove = "media-access.media.remove";
    public const string MediaClear = "media-access.media.clear";

    public const string ProviderList = "media-access.provider.list";
    public const string ProviderAdd = "media-access.provider.add";
    public const string ProviderRemove = "media-access.provider.remove";
    public const string ProviderClear = "media-access.provider.clear";

    public const string AgeList = "media-access.age.list";
    public const string AgeAdd = "media-access.age.add";
    public const string AgeRemove = "media-access.age.remove";

    public const string QueueGroup = "databridge-media-access";
}

// --- Watch-time gate -------------------------------------------------------

public sealed record MediaAccessCheckRequestMessage
{
    public required Guid MediaGuid { get; init; }

    public IReadOnlyList<string> UserGroups { get; init; } = [];
}

public sealed record MediaAccessCheckResponseMessage
{
    public bool IsAllowed { get; init; }

    /// <summary>Diagnostic reason a check failed (logged server-side; not surfaced to clients).</summary>
    public string? FailureReason { get; init; }

    /// <summary>Set when the check itself could not be evaluated (e.g. DB error).</summary>
    public string? ErrorCode { get; init; }
}

// --- Per-media restrictions ------------------------------------------------

public sealed record MediaAccessMediaListRequestMessage
{
    public required Guid MediaGuid { get; init; }
}

public sealed record MediaAccessMediaMutateRequestMessage
{
    public required Guid MediaGuid { get; init; }

    public string? GroupName { get; init; }

    public string? CreatedBySubject { get; init; }
}

// --- Provider restrictions -------------------------------------------------

public sealed record MediaAccessProviderListRequestMessage;

public sealed record MediaAccessProviderMutateRequestMessage
{
    public string? Provider { get; init; }

    public string? GroupName { get; init; }

    public string? CreatedBySubject { get; init; }
}

public sealed record MediaAccessProviderEntryDto
{
    public required string Provider { get; init; }

    public IReadOnlyList<string> Groups { get; init; } = [];
}

// --- Age-limit policies ----------------------------------------------------

public sealed record MediaAccessAgeListRequestMessage;

public sealed record MediaAccessAgeMutateRequestMessage
{
    public int Threshold { get; init; }

    public string? GroupName { get; init; }

    public string? CreatedBySubject { get; init; }
}

public sealed record MediaAccessAgePolicyDto
{
    public int Threshold { get; init; }

    public IReadOnlyList<string> Groups { get; init; } = [];
}

// --- Shared response -------------------------------------------------------

/// <summary>
/// Generic response for all media-access management operations. List operations populate the
/// matching collection; mutations set <see cref="Success"/>.
/// </summary>
public sealed record MediaAccessOperationResponseMessage
{
    public bool Success { get; init; }

    public IReadOnlyList<string>? Groups { get; init; }

    public IReadOnlyList<MediaAccessProviderEntryDto>? Providers { get; init; }

    public IReadOnlyList<MediaAccessAgePolicyDto>? AgePolicies { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
