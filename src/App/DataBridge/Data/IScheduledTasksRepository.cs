using NodaTime;
using Shared.Database;

namespace DataBridge.Data;

public interface IScheduledTasksRepository
{
    Task<ScheduledTaskEntity?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledTaskEntity>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledTaskEntity>> ListActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledTaskEntity>> ListOverdueAsync(CancellationToken cancellationToken = default);
    Task<ScheduledTaskEntity> CreateAsync(ScheduledTaskEntity entity, CancellationToken cancellationToken = default);
    Task<ScheduledTaskEntity?> UpdateAsync(ScheduledTaskEntity entity, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task MarkAttemptAsync(string key, Instant attemptedAt, CancellationToken cancellationToken = default);
    Task<ScheduledTaskEntity?> MarkSuccessAsync(string key, Instant succeededAt, CancellationToken cancellationToken = default);
}
