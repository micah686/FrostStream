using Shared.Database;

namespace DataBridge.Data;

public interface IDownloadConfigSetsRepository
{
    Task<DownloadConfigSetEntity?> GetAsync(string ownerSubject, string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadConfigSetEntity>> ListAsync(string ownerSubject, CancellationToken cancellationToken = default);

    Task<DownloadConfigSetEntity> CreateAsync(DownloadConfigSetEntity entity, CancellationToken cancellationToken = default);

    Task<DownloadConfigSetEntity?> UpdateAsync(DownloadConfigSetEntity entity, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string ownerSubject, string key, CancellationToken cancellationToken = default);
}
