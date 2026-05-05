using Shared.Metadata;

namespace DataBridge.Data;

/// <summary>
/// Writes all <c>metadata.*</c> tables for a given <c>media_guid</c> atomically.
/// The entire write is wrapped in a single PostgreSQL transaction — either all rows
/// commit or none do.
/// </summary>
public interface IMetadataRepository
{
    Task WriteMetadataAsync(Guid mediaGuid, CapturedMediaMetadata metadata, CancellationToken ct = default);
}
