namespace Shared.Imports;

/// <summary>
/// Sidecar file references (info.json / thumbnail / captions) that travel with a local import
/// item from scan discovery through the per-item prepare/upload flow.
/// </summary>
public sealed record LocalMediaImportManifestSidecars
{
    public string? InfoJson { get; init; }

    public string? Thumbnail { get; init; }

    public IReadOnlyList<LocalMediaImportCaptionSidecar> Captions { get; init; } = [];
}

public sealed record LocalMediaImportCaptionSidecar
{
    public required string File { get; init; }

    public string? LanguageCode { get; init; }

    public string? CaptionType { get; init; }

    public string? Name { get; init; }
}
