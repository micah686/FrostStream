
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

public static class ArtifactStorageTopology
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

}
