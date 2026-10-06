using Shared.Messaging;

namespace Scheduler.Databridge;

public interface IDatabridgeClient
{
    Task<ScheduleOperationResponseMessage?> GetScheduleAsync(string key, CancellationToken cancellationToken = default);
    Task<ScheduleOperationResponseMessage?> ListActiveSchedulesAsync(CancellationToken cancellationToken = default);
    Task<ScheduleOperationResponseMessage?> ListOverdueSchedulesAsync(CancellationToken cancellationToken = default);
}
