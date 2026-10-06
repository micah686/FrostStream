using Shared.Database;

namespace DataBridge.Data;

public interface IOptionPresetsRepository
{
    Task<OptionPresetEntity?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OptionPresetEntity>> ListAsync(CancellationToken cancellationToken = default);

    Task<OptionPresetEntity> CreateAsync(string key, string name, string? description, string ytdlpOptionsJson, CancellationToken cancellationToken = default);

    Task<OptionPresetEntity?> UpdateAsync(string key, string name, string? description, string ytdlpOptionsJson, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
