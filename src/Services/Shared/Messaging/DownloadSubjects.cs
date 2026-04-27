namespace Shared.Messaging;

public static class DownloadSubjects
{
    public const string DownloadRequested              = "download.requested";

    public const string FetchMetadataCommand           = "download.cmd.fetch-metadata";
    public const string DownloadVideoCommand           = "download.cmd.download-video";
    public const string UploadObjectCommand            = "download.cmd.upload-object";
    public const string DeleteTempFileCommand          = "download.cmd.delete-temp-file";
    public const string DeleteUploadedObjectCommand    = "download.cmd.delete-uploaded-object";

    public const string MetadataFetched                = "download.evt.metadata-fetched";
    public const string MetadataFetchFailed            = "download.evt.metadata-fetch-failed";
    public const string DownloadCompleted              = "download.evt.download-completed";
    public const string DownloadFailed                 = "download.evt.download-failed";
    public const string UploadCompleted                = "download.evt.upload-completed";
    public const string UploadFailed                   = "download.evt.upload-failed";
    public const string TempFileDeleted                = "download.evt.temp-file-deleted";
    public const string TempFileDeleteFailed           = "download.evt.temp-file-delete-failed";
    public const string UploadedObjectDeleted          = "download.evt.uploaded-object-deleted";
    public const string UploadedObjectDeleteFailed     = "download.evt.uploaded-object-delete-failed";
}
