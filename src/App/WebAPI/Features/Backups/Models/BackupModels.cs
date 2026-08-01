namespace WebAPI.Features.Backups.Models;

public sealed record CreateBackupRequest(string? Name, string? Mode);

public sealed record BackupJobResponse(
    Guid JobId,
    string Status,
    string? ArchivePath,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record BackupSummaryResponse(
    string ArchivePath,
    DateTimeOffset? CreatedAt,
    bool MediaIncluded,
    int SchemaVersion,
    string Mode);

public sealed record VerifyBackupRequest(string ArchivePath);

public sealed record VerifyBackupResponse(bool Success, string? ErrorMessage);

public sealed record RestorePlanRequest(
    string ArchivePath,
    IReadOnlyDictionary<string, string?>? Options = null);

public sealed record RestorePlanResponse(
    bool PreflightOk,
    string Explanation,
    string RestoreCommand,
    IReadOnlyList<RestorePlanOptionResponse> Options,
    string? ErrorMessage);

public sealed record RestorePlanOptionResponse(
    string Key,
    string Label,
    string Description,
    string InputType,
    string? Value,
    string? Placeholder,
    bool Required);
