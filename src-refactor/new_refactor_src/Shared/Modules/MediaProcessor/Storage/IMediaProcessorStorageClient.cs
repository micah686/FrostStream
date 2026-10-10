namespace MediaProcessor.Storage;

public interface IMediaProcessorStorageClient
{
    Task DownloadToFileAsync(string storageKey, string storagePath, string localPath, CancellationToken cancellationToken);
    Task UploadFromFileAsync(string localPath, string storageKey, string storagePath, CancellationToken cancellationToken);
}
