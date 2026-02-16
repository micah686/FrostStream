using FlySwattr.NATS.Abstractions;

namespace Shared.Topology;

/// <summary>
/// Defines the JetStream topology for job processing: the froststream-jobs stream
/// and the file-processors durable consumer.
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
            AckWait = TimeSpan.FromSeconds(30),
            MaxDeliver = 5
        }
    ];
}
