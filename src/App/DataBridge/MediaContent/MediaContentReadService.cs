using DataBridge.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Messaging;

namespace DataBridge.MediaContent;

public sealed class MediaContentReadService(DataBridgeDbContext dbContext) : IMediaContentReadService
{
    public async Task<MediaContentLocationDto?> ResolveAsync(
        Guid mediaGuid,
        string? storageKey,
        int? version,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.MediaContentIdVersions
            .AsNoTracking()
            .Where(content => content.MediaGuid == mediaGuid);

        if (!string.IsNullOrWhiteSpace(storageKey))
        {
            query = query.Where(content => content.StorageKey == storageKey);
        }

        if (version is not null)
        {
            query = query.Where(content => content.VersionNum == version.Value);
        }

        return await query
            .OrderByDescending(content => content.VersionNum)
            .Select(content => new MediaContentLocationDto
            {
                MediaGuid = content.MediaGuid,
                StorageKey = content.StorageKey,
                StoragePath = content.StoragePath,
                Version = content.VersionNum
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
