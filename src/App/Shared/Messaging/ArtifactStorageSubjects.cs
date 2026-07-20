using Conduit.NATS;

namespace Shared.Messaging;

public static class ArtifactStorageSubjects
{
    public const string UploadObjectCommand = "artifact-storage.v1.command.upload";
    public const string DeleteTempFileCommand = "artifact-storage.v1.command.delete-temp";
    public const string DeleteUploadedObjectCommand = "artifact-storage.v1.command.delete-object";
    public const string UploadCompleted = "artifact-storage.v1.event.upload.succeeded";
    public const string UploadFailed = "artifact-storage.v1.event.upload.failed";
    public const string TempFileDeleted = "artifact-storage.v1.event.delete-temp.succeeded";
    public const string TempFileDeleteFailed = "artifact-storage.v1.event.delete-temp.failed";
    public const string UploadedObjectDeleted = "artifact-storage.v1.event.delete-object.succeeded";
    public const string UploadedObjectDeleteFailed = "artifact-storage.v1.event.delete-object.failed";

    public static string Tagged(string subject, string tag) => $"{subject}.{tag}";
    public static string UploadObjectCommandForTag(string tag) => Tagged(UploadObjectCommand, tag);
    public static string DeleteTempFileCommandForTag(string tag) => Tagged(DeleteTempFileCommand, tag);
    public static string DeleteUploadedObjectCommandForTag(string tag) => Tagged(DeleteUploadedObjectCommand, tag);
}

public sealed class ArtifactStorageTopology : ITopologySource
{
    public const string StreamNameValue = "FROSTSTREAM_ARTIFACT_STORAGE_V1";
    public const string SubjectFilter = "artifact-storage.v1.>";
    public const string WorkerUploadConsumer = "worker-artifact-upload-v1";
    public const string WorkerDeleteTempConsumer = "worker-artifact-delete-temp-v1";
    public const string WorkerDeleteObjectConsumer = "worker-artifact-delete-object-v1";
    public const string DownloadUploadCompletedConsumer = "databridge-download-v2-artifact-upload-completed";
    public const string DownloadUploadFailedConsumer = "databridge-download-v2-artifact-upload-failed";
    public const string DownloadTempDeletedConsumer = "databridge-download-v2-temp-deleted";
    public const string DownloadTempDeleteFailedConsumer = "databridge-download-v2-temp-delete-failed";
    public const string DownloadObjectDeletedConsumer = "databridge-download-v2-object-deleted";
    public const string DownloadObjectDeleteFailedConsumer = "databridge-download-v2-object-delete-failed";
    public const string LocalImportUploadCompletedConsumer = "databridge-local-import-artifact-upload-completed";
    public const string LocalImportUploadFailedConsumer = "databridge-local-import-artifact-upload-failed";
    public const string LocalImportObjectDeletedConsumer = "databridge-local-import-artifact-object-deleted";
    public const string LocalImportObjectDeleteFailedConsumer = "databridge-local-import-artifact-object-delete-failed";

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
