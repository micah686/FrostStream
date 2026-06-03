namespace DataBridge.Messaging;

public sealed record OrphanMetadataCleanupResult(
    long RecordedMetadataWithoutMediaCount,
    long RecordedMediaWithoutMetadataCount,
    long ResolvedCount,
    long MovedFileCount,
    long MoveFailedCount,
    long DeletedFileCount,
    long FileDeleteFailedCount,
    long DeletedMediaCount);
