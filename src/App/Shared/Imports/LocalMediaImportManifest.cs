using NodaTime;
using Shared.Metadata;

namespace Shared.Imports;

public sealed record LocalMediaImportManifest
{
    public string? SourceRoot { get; init; }

    public string? StorageKey { get; init; }

    public IReadOnlyList<LocalMediaImportManifestItem> Items { get; init; } = [];
}

public sealed record LocalMediaImportManifestItem
{
    public required string File { get; init; }

    public string? Provider { get; init; }

    public string? SourceMediaId { get; init; }

    public Instant? SourceLastModified { get; init; }

    public string? SourceUrl { get; init; }

    public string? Title { get; init; }

    public CapturedMediaMetadata? Metadata { get; init; }

    public LocalMediaImportManifestSidecars? Sidecars { get; init; }
}

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
