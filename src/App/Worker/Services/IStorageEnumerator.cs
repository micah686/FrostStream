namespace Worker.Services;

public interface IStorageEnumerator
{
    IAsyncEnumerable<string> EnumerateFilePathsAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}
