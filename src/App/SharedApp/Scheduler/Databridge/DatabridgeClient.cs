using Scheduler.Messaging;
using Shared.Messaging;

namespace Scheduler.Databridge;

public sealed class DatabridgeClient(INatsRequestClient requestClient) : IDatabridgeClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public Task<ScheduleOperationResponseMessage?> GetScheduleAsync(string key, CancellationToken cancellationToken = default)
        => requestClient.RequestAsync<ScheduleGetRequestMessage, ScheduleOperationResponseMessage>(
            ScheduleSubjects.Get,
            new ScheduleGetRequestMessage { Key = key },
            RequestTimeout,
            cancellationToken);

    public Task<ScheduleOperationResponseMessage?> ListActiveSchedulesAsync(CancellationToken cancellationToken = default)
        => requestClient.RequestAsync<ScheduleListActiveRequestMessage, ScheduleOperationResponseMessage>(
            ScheduleSubjects.ActiveList,
            new ScheduleListActiveRequestMessage(),
            RequestTimeout,
            cancellationToken);

    public Task<ScheduleOperationResponseMessage?> ListOverdueSchedulesAsync(CancellationToken cancellationToken = default)
        => requestClient.RequestAsync<ScheduleListOverdueRequestMessage, ScheduleOperationResponseMessage>(
            ScheduleSubjects.Overdue,
            new ScheduleListOverdueRequestMessage(),
            RequestTimeout,
            cancellationToken);
}
