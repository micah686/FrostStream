namespace WebAPI.Features.Imports.Models;

public sealed class LocalMediaImportRequest
{
    /// <summary>Destination storage target the discovered files are copied into.</summary>
    public string? StorageKey { get; init; }

    /// <summary>
    /// Optional worker tag to run the import on. When omitted, the destination storage target's
    /// configured worker tag is used (or any worker if that is also unset).
    /// </summary>
    public string? WorkerTag { get; init; }

    /// <summary>Optional free-form note recorded alongside the batch.</summary>
    public string? RequestedBy { get; init; }
}

public sealed record LocalMediaImportRequestResponse(Guid BatchId, Guid CorrelationId);
