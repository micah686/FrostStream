using System.Text.Json;

namespace Shared.Messaging;

public static class NotificationSubjects
{
    public const string GetPreferences = "notifications.preferences.get";
    public const string UpdatePreferences = "notifications.preferences.update";
    public const string UpsertProvider = "notifications.providers.upsert";
    public const string DeleteProvider = "notifications.providers.delete";
    public const string Test = "notifications.test";
    public const string DispatchAdmin = "notifications.dispatch.admin";
}

public static class NotificationEventKeys
{
    public const string DownloadCompleted = "download.completed";
    public const string DownloadFailedPermanent = "download.failed-permanent";
    public const string DownloadDeadLettered = "download.dead-lettered";
    public const string DownloadProviderHalted = "download.provider-halted";
    public const string ScheduleFailed = "schedule.failed";
    public const string WorkerUnavailable = "worker.unavailable";
    public const string StorageFailedPermanent = "storage.failed-permanent";
    public const string BackupFailed = "backup.failed";
    public const string IndexRebuildFailed = "index.rebuild.failed";
    public const string SystemIntegrationFailed = "system.integration.failed";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        DownloadCompleted,
        DownloadFailedPermanent,
        DownloadDeadLettered,
        DownloadProviderHalted,
        ScheduleFailed,
        WorkerUnavailable,
        StorageFailedPermanent,
        BackupFailed,
        IndexRebuildFailed,
        SystemIntegrationFailed
    };
}

public sealed record NotificationPreferencesDto
{
    public int Version { get; init; } = 1;

    public bool Enabled { get; init; }

    public IReadOnlyList<NotificationProviderDto> Providers { get; init; } = [];
}

public sealed record NotificationProviderDto
{
    public required string ProviderKey { get; init; }

    public required string ProviderKind { get; init; }

    public bool Enabled { get; init; } = true;

    public string? DisplayName { get; init; }

    public string? DefaultTo { get; init; }

    public IReadOnlyList<string> EventKeys { get; init; } = [];

    public JsonElement NotifyConfig { get; init; }
}

public sealed record NotificationGetPreferencesRequestMessage
{
    public required string OwnerSubject { get; init; }
}

public sealed record NotificationUpdatePreferencesRequestMessage
{
    public required string OwnerSubject { get; init; }

    public required NotificationPreferencesDto Preferences { get; init; }
}

public sealed record NotificationUpsertProviderRequestMessage
{
    public required string OwnerSubject { get; init; }

    public required NotificationProviderDto Provider { get; init; }
}

public sealed record NotificationDeleteProviderRequestMessage
{
    public required string OwnerSubject { get; init; }

    public required string ProviderKey { get; init; }
}

public sealed record NotificationTestRequestMessage
{
    public required string OwnerSubject { get; init; }

    public required string ProviderKey { get; init; }

    public string? Subject { get; init; }

    public string? Body { get; init; }
}

public sealed record NotificationDispatchAdminEventMessage
{
    public required string EventKey { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }
}

public sealed record NotificationOperationResponseMessage
{
    public bool Success { get; init; }

    public NotificationPreferencesDto? Preferences { get; init; }

    public NotificationProviderDto? Provider { get; init; }

    public IReadOnlyList<NotificationProviderDto>? Providers { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
