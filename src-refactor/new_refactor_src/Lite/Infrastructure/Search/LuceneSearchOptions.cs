namespace FrostStream.Lite.Infrastructure.Search;

public sealed class LuceneSearchOptions
{
    public required string DirectoryPath { get; init; }
    public double RamBufferMiB { get; init; } = 16;
    public int MaximumPageDepth { get; init; } = 100_000;
}
