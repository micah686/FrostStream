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

public sealed record RestorePlanRequest(string ArchivePath);

public sealed record RestorePlanResponse(
    bool PreflightOk,
    string RestoreCommand,
    string? ErrorMessage);
