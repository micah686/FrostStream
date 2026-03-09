using FlySwattr.NATS.Abstractions;

namespace Shared.Topology;

/// <summary>
/// Defines the JetStream topology for job processing: the froststream-jobs stream,
/// froststream-dlq stream, froststream-progress stream, and associated consumers.
/// </summary>
public class JobsTopology : ITopologySource
{
    public IEnumerable<StreamSpec> GetStreams() =>
    [
        new StreamSpec
        {
            Name = StreamName.From(Streams.Jobs),
            Subjects = [Subjects.DownloadFile],
            RetentionPolicy = StreamRetention.WorkQueue
        },
        new StreamSpec
        {
            Name = StreamName.From(Streams.DeadLetter),
            Subjects = [Subjects.DeadLetter],
            RetentionPolicy = StreamRetention.Limits,
            MaxBytes = 10L * 1024 * 1024 * 1024, // 10GB limit for DLQ
            MaxAge = TimeSpan.FromDays(30)
        },
        new StreamSpec
        {
            Name = StreamName.From(Streams.Progress),
            Subjects = [Subjects.JobProgressStream],
            RetentionPolicy = StreamRetention.Limits,
            MaxBytes = 5L * 1024 * 1024 * 1024, // 5GB limit for progress
            MaxAge = TimeSpan.FromDays(7)
        }
    ];

    public IEnumerable<ConsumerSpec> GetConsumers() =>
    [
        new ConsumerSpec
        {
            StreamName = StreamName.From(Streams.Jobs),
            DurableName = ConsumerName.From(Consumers.FileProcessors),
            FilterSubject = Subjects.DownloadFile,
            AckPolicy = AckPolicy.Explicit,
            AckWait = TimeSpan.FromMinutes(30),  // Increased for large downloads
            MaxDeliver = 5,
            Backoff =
            [
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(60)
            ]
        }
    ];
}
