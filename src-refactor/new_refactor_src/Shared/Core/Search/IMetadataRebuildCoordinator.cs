namespace DataBridge.Search;
public interface IMetadataRebuildCoordinator
{
    MetadataRebuildStartResult StartRebuild(string reason);
    Task<MetadataRebuildStartResult> RebuildAsync(string reason, CancellationToken ct = default);
}

public sealed record MetadataRebuildStartResult(bool Accepted, string? ErrorMessage = null);

