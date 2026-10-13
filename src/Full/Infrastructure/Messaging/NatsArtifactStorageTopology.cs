using Conduit.NATS;
using Shared.Messaging;
using static Shared.Messaging.ArtifactStorageTopology;

namespace FrostStream.Full.Infrastructure;

public sealed class NatsArtifactStorageTopology : ITopologySource
{
    public IEnumerable<StreamSpec> GetStreams()
    {
        yield return new StreamSpec
        {
            Name = StreamName.From(StreamNameValue),
            Subjects = [SubjectFilter],
            MaxAge = TimeSpan.FromDays(30),
            RetentionPolicy = StreamRetention.Limits,
            StorageType = StorageType.File,
            Replicas = 1
        };
    }

    public IEnumerable<ConsumerSpec> GetConsumers()
    {
        yield return Worker(WorkerUploadConsumer, ArtifactStorageSubjects.UploadObjectCommand);
        yield return Worker(WorkerDeleteTempConsumer, ArtifactStorageSubjects.DeleteTempFileCommand);
        yield return Worker(WorkerDeleteObjectConsumer, ArtifactStorageSubjects.DeleteUploadedObjectCommand);
        yield return DataBridge(DownloadUploadCompletedConsumer, ArtifactStorageSubjects.UploadCompleted);
        yield return DataBridge(DownloadUploadFailedConsumer, ArtifactStorageSubjects.UploadFailed);
        yield return DataBridge(DownloadTempDeletedConsumer, ArtifactStorageSubjects.TempFileDeleted);
        yield return DataBridge(DownloadTempDeleteFailedConsumer, ArtifactStorageSubjects.TempFileDeleteFailed);
        yield return DataBridge(DownloadObjectDeletedConsumer, ArtifactStorageSubjects.UploadedObjectDeleted);
        yield return DataBridge(DownloadObjectDeleteFailedConsumer, ArtifactStorageSubjects.UploadedObjectDeleteFailed);
        yield return DataBridge(LocalImportUploadCompletedConsumer, ArtifactStorageSubjects.UploadCompleted);
        yield return DataBridge(LocalImportUploadFailedConsumer, ArtifactStorageSubjects.UploadFailed);
        yield return DataBridge(LocalImportObjectDeletedConsumer, ArtifactStorageSubjects.UploadedObjectDeleted);
        yield return DataBridge(LocalImportObjectDeleteFailedConsumer, ArtifactStorageSubjects.UploadedObjectDeleteFailed);
    }

    public static ConsumerSpec TaggedWorkerConsumerSpec(string durableName, string subject, string tag) => new()
    {
        StreamName = StreamName.From(StreamNameValue),
        DurableName = ConsumerName.From($"{durableName}-{tag}"),
        DeliverGroup = QueueGroup.From($"artifact-workers-{tag}"),
        FilterSubject = ArtifactStorageSubjects.Tagged(subject, tag),
        AckPolicy = AckPolicy.Explicit,
        AckWait = TimeSpan.FromMinutes(2),
        MaxDeliver = 10
    };

    private static ConsumerSpec Worker(string durableName, string subject) => new()
    {
        StreamName = StreamName.From(StreamNameValue),
        DurableName = ConsumerName.From(durableName),
        DeliverGroup = QueueGroup.From("artifact-workers"),
        FilterSubject = subject,
        AckPolicy = AckPolicy.Explicit,
        AckWait = TimeSpan.FromMinutes(2),
        MaxDeliver = 10
    };

    private static ConsumerSpec DataBridge(string durableName, string subject) => new()
    {
        StreamName = StreamName.From(StreamNameValue),
        DurableName = ConsumerName.From(durableName),
        DeliverGroup = QueueGroup.From("databridge-artifact-events"),
        FilterSubject = subject,
        AckPolicy = AckPolicy.Explicit,
        AckWait = TimeSpan.FromMinutes(2),
        MaxDeliver = 10
    };
}
