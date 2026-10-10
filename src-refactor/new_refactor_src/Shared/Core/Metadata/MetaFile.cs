namespace Shared.Metadata;

/// <summary>Small metadata snapshot carried through the download saga.</summary>
public sealed record MetaFile
{
    public string? Title { get; init; }
    public string OriginalUrl { get; init; } = "";
}
