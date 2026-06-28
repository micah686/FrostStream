using Microsoft.AspNetCore.Http;

namespace WebAPI.Features.Imports.Models;

public sealed class LocalMediaImportRequest
{
    public IFormFile? Manifest { get; init; }

    public string? SourceRoot { get; init; }

    public string? StorageKey { get; init; }

    public string? RequestedBy { get; init; }
}

public sealed record LocalMediaImportRequestResponse(Guid BatchId, Guid CorrelationId);
