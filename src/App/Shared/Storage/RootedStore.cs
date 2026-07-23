using FluentStorage;
using FluentStorage.Model;
using FluentStorage.Storage;
using FluentStorage.Streaming;

namespace Shared.Storage;

/// <summary>
/// Restricts a provider store to a configured remote root while keeping FrostStream object paths relative.
/// </summary>
internal sealed class RootedStore(IStore inner, string rootPath) : StoreBase
{
    private readonly string _rootPath = StorageObjectPath.Normalize(rootPath);

    public override void Dispose()
    {
        inner.Dispose();
        base.Dispose();
    }

    public override Task<object> GetClient() => inner.GetClient();
    public override Task<bool> IsFileSystem() => inner.IsFileSystem();
    public override Task<bool> IsSeekable() => inner.IsSeekable();
    public override Task<bool> IsVersioned() => inner.IsVersioned();
    public override Task<bool> IsTagged() => inner.IsTagged();
    public override Task<bool> IsTiered() => inner.IsTiered();

    public override Task<Stream> OpenRead(string objectPath, CancellationToken cancellationToken = default)
        => inner.OpenRead(Resolve(objectPath), cancellationToken);

    public override Task<Stream> OpenWrite(string objectPath, bool overwrite, CancellationToken cancellationToken = default)
        => inner.OpenWrite(Resolve(objectPath), overwrite, cancellationToken);

    public override Task<Stream> OpenRange(string path, long offset, long length, CancellationToken cancellationToken = default)
        => inner.OpenRange(Resolve(path), offset, length, cancellationToken);

    public override Task<SeekableStream> OpenSeekable(string path, int bufferSize = 65536, CancellationToken cancellationToken = default)
        => inner.OpenSeekable(Resolve(path), bufferSize, cancellationToken);

    public override Task<long> GetObjectLength(string fullPath, long defaultValue = -1, CancellationToken cancellationToken = default)
        => inner.GetObjectLength(Resolve(fullPath), defaultValue, cancellationToken);

    public override async Task<List<StoreObject>> ListObjects(
        StorageListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var rootedOptions = options?.Clone() ?? new StorageListOptions();
        rootedOptions.FolderPath = Resolve(rootedOptions.FolderPath);
        var objects = await inner.ListObjects(rootedOptions, cancellationToken).ConfigureAwait(false);
        foreach (var item in objects)
        {
            item.SetFullPath(ToRelative(item.FullPath));
        }
        return objects;
    }

    public override Task SetObject(string objectPath, Stream sourceStream, bool append, CancellationToken cancellationToken = default)
        => inner.SetObject(Resolve(objectPath), sourceStream, append, cancellationToken);

    public override Task SetObject(
        string objectPath,
        Stream dataStream,
        string? contentType,
        bool append,
        CancellationToken cancellationToken = default)
        => inner.SetObject(Resolve(objectPath), dataStream, contentType, append, cancellationToken);

    public override Task<bool> ObjectExists(string objectPath, CancellationToken cancellationToken = default)
        => inner.ObjectExists(Resolve(objectPath), cancellationToken);

    public override Task<List<bool>> ObjectsExists(IEnumerable<string> objectPaths, CancellationToken cancellationToken = default)
        => inner.ObjectsExists(objectPaths.Select(Resolve), cancellationToken);

    public override Task DeleteObject(string objectPath, CancellationToken cancellationToken = default)
        => inner.DeleteObject(Resolve(objectPath), cancellationToken);

    public override Task DeleteObjects(IEnumerable<string> objectPaths, CancellationToken cancellationToken = default)
        => inner.DeleteObjects(objectPaths.Select(Resolve), cancellationToken);

    public override Task<bool> MoveObject(
        string oldPath,
        string newPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
        => inner.MoveObject(Resolve(oldPath), Resolve(newPath), overwrite, cancellationToken);

    public override Task CreateDirectory(string folderPath, bool force, CancellationToken cancellationToken = default)
        => inner.CreateDirectory(Resolve(folderPath), force, cancellationToken);

    public override Task DeleteDirectory(string folderPath, bool recursive, CancellationToken cancellationToken = default)
        => inner.DeleteDirectory(Resolve(folderPath), recursive, cancellationToken);

    public override Task<bool> DirectoryExists(string folderPath, CancellationToken cancellationToken = default)
        => inner.DirectoryExists(Resolve(folderPath), cancellationToken);

    public override Task MoveDirectory(string sourceFolderPath, string destinationFolderPath, CancellationToken cancellationToken = default)
        => inner.MoveDirectory(Resolve(sourceFolderPath), Resolve(destinationFolderPath), cancellationToken);

    private string Resolve(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? _rootPath
            : StorageObjectPath.Combine(_rootPath, StorageObjectPath.Normalize(path));

    private string ToRelative(string path)
    {
        var normalized = StorageObjectPath.Normalize(path);
        if (string.Equals(normalized, _rootPath, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = _rootPath + StoragePath.PathSeparator;
        return normalized.StartsWith(prefix, StringComparison.Ordinal)
            ? normalized[prefix.Length..]
            : normalized;
    }
}
