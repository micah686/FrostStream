namespace FrostStream.Shared;

/// <summary>
/// Request to reserve a transfer slot on the DataBridge.
/// </summary>
public record TransferRequest(Guid JobId, string WorkerId, long SizeBytes);

/// <summary>
/// Base reply for transfer‑reservation requests.
/// </summary>
public abstract record TransferReply(Guid JobId);

/// <summary>
/// Reply indicating a transfer slot has been granted.
/// Includes the lease identifier and TCP port to connect to.
/// </summary>
public sealed record TransferGranted(Guid JobId, Guid LeaseId, DateTime ExpiresAtUtc, int Port)
    : TransferReply(JobId);

/// <summary>
/// Reply indicating that no slots are currently available.
/// Caller should wait the specified seconds before retrying.
/// </summary>
public sealed record TransferDenied(Guid JobId, int RetryAfterSeconds)
    : TransferReply(JobId);
