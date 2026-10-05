using Shared.Storage;

namespace MediaProcessor.Storage;

/// <summary>Lite moves bytes through the shared provider directly, independent of HTTP readiness or addresses.</summary>
public sealed class LocalMediaProcessorStorageClient(IStoreProvider provider) : IMediaProcessorStorageClient
{
    private static void Validate(string storageKey, string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (storagePath.StartsWith('/') || storagePath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Storage path is invalid.", nameof(storagePath));
        StorageObjectPath.Normalize(storagePath);
    }

    public async Task DownloadToFileAsync(string storageKey, string storagePath, string localPath, CancellationToken cancellationToken)
    {
        Validate(storageKey, storagePath);
        var store = await provider.GetAsync(storageKey, cancellationToken);
        await using var source = await store.OpenRead(storagePath, cancellationToken);
        if (source is null) throw new FileNotFoundException("The source media blob was not found.", storagePath);
        await using var target = File.Create(localPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    public async Task UploadFromFileAsync(string localPath, string storageKey, string storagePath, CancellationToken cancellationToken)
    {
        Validate(storageKey, storagePath);
        var store = await provider.GetAsync(storageKey, cancellationToken);
        await using var source = File.OpenRead(localPath);
        await store.SetObject(storagePath, source, append: false, cancellationToken);
    }
}
