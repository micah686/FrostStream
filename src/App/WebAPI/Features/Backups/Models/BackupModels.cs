namespace WebAPI.Features.Backups.Models;

public sealed record CreateBackupRequest(string? Name, string? Type);

public sealed record BackupJobResponse(
    Guid JobId,
    string Kind,
    string? Type,
    string Status,
    string? Name,
    string? Label,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> Progress);

public sealed record BackupSummaryResponse(
    string Label,
    string Type,
    string? Name,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DatabaseSize,
    long? RepositorySize,
    string? WalStart,
    string? WalStop,
    bool HasError,
    bool OpenBaoExportPresent);

public sealed record PitrWindowResponse(DateTimeOffset? Earliest, DateTimeOffset? LatestApprox);

public sealed record BackupRepositoryResponse(
    bool RepositoryOk,
    string? StatusMessage,
    IReadOnlyList<BackupSummaryResponse> Backups,
    PitrWindowResponse PitrWindow);

public sealed record VerifyBackupRequest(string? Label = null, bool Deep = false);
