using DiscUtils.Nfs;
using FluentStorage.Enums;
using FluentStorage.Model;
using FluentStorage.Storage;
using StorageExtensions.Internal;

namespace StorageExtensions.Nfs;

/// <summary>
/// A FluentStorage store backed by an NFSv3 export reached directly over the network.
/// Nothing is mounted by the operating system, so the process needs no root privileges
/// and no host-level configuration.
/// </summary>
/// <remarks>
/// Two server-side requirements are worth checking before pointing this at an export:
/// the export must accept connections from unprivileged source ports (the <c>insecure</c>
/// option on Linux), because the NFS client never binds a reserved port; and it must
/// support READDIRPLUS, which is the only directory enumeration the client issues.
/// Everything other than listing works without READDIRPLUS.
/// </remarks>
public class NfsStore : StoreBase
{
    private readonly NfsConnection _connection;
    private readonly string? _basePath;

    /// <summary>
    /// Creates a store for the export described by <paramref name="options"/>. The
    /// connection and MOUNT handshake happen lazily on first use.
    /// </summary>
    public NfsStore(NfsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("NFS storage requires a host.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Export))
        {
            throw new ArgumentException("NFS storage requires an export path.", nameof(options));
        }

        _connection = new NfsConnection(options);
        _basePath = options.BasePath;
    }

    /// <summary>
    /// Lists the exports a server offers. Useful for validating configuration before a
    /// store is created.
    /// </summary>
    public static IEnumerable<string> GetExports(string host) => NfsFileSystem.GetExports(host);

    /// <summary>An NFS export is a real file system, not an object bucket.</summary>
    public override Task<bool> IsFileSystem() => Task.FromResult(true);

    /// <summary>NFSv3 reads carry an explicit offset, so random access is native.</summary>
    public override Task<bool> IsSeekable() => Task.FromResult(true);

    /// <summary>Returns the connected <see cref="NfsFileSystem"/>.</summary>
    public override Task<object> GetClient()
        => _connection.ExecuteAsync(object (fs) => fs);

    /// <summary>
    /// Lists one folder. Recursion, filtering, and result limits are applied by
    /// <see cref="StoreBase"/> on top of this.
    /// </summary>
    protected override async Task<List<StoreObject>> ListPath(
        string path,
        StorageListOptions options,
        CancellationToken cancellationToken = default)
    {
        var folder = Resolve(path);

        var entries = await _connection.ExecuteAsync(fs =>
        {
            var listing = new List<(string Name, bool IsFolder, long? Size, DateTime? Modified)>();

            if (!TryGetAttributes(fs, folder, out var folderAttributes) ||
                !folderAttributes.HasFlag(FileAttributes.Directory))
            {
                return listing;
            }

            try
            {
                // The public NFS surface returns names only, so size and timestamps cost an
                // extra GETATTR per entry. They are gathered here, inside one lock acquisition,
                // rather than leaving callers to fetch them one object at a time.
                foreach (var entry in fs.GetFileSystemEntries(folder))
                {
                    var segments = RemotePath.Split(entry);
                    if (segments.Length == 0)
                    {
                        continue;
                    }

                    var name = segments[^1];
                    var entryPath = RemotePath.ToStoragePath([.. RemotePath.Split(folder), name]);

                    if (!TryGetAttributes(fs, entryPath, out var attributes))
                    {
                        // Vanished between the listing and the stat; skip it rather than fail.
                        continue;
                    }

                    var isFolder = attributes.HasFlag(FileAttributes.Directory);
                    listing.Add((
                        name,
                        isFolder,
                        isFolder ? null : fs.GetFileLength(entryPath),
                        fs.GetLastWriteTimeUtc(entryPath)));
                }
            }
            catch (Exception ex) when (IsReadDirPlusUnsupported(ex))
            {
                throw new NotSupportedException(
                    $"The NFS server at '{_connection.Options.Host}' rejected READDIRPLUS, which is the only directory " +
                    "enumeration the underlying NFS client issues. Enable READDIRPLUS on the export " +
                    "(on Linux, check the nfsd.nfs3_readdirplus setting) or use a different protocol " +
                    "for this share. Reads, writes, and metadata lookups are unaffected.", ex);
            }

            return listing;
        }, cancellationToken).ConfigureAwait(false);

        var result = new List<StoreObject>();
        foreach (var (name, isFolder, size, modified) in entries)
        {
            var item = new StoreObject(
                RemotePath.CombineStoragePath(path, name),
                isFolder ? StorageObjectType.Folder : StorageObjectType.File)
            {
                Size = size,
                DateModified = ToDateTimeOffset(modified)
            };

            if (options.IsMatch(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override Task<bool> ObjectExists(string objectPath, CancellationToken cancellationToken = default)
        => _connection.ExecuteAsync(fs => TryGetAttributes(fs, Resolve(objectPath), out _), cancellationToken);

    /// <inheritdoc />
    public override Task<bool> DirectoryExists(string folderPath, CancellationToken cancellationToken = default)
        => _connection.ExecuteAsync(
            fs => TryGetAttributes(fs, Resolve(folderPath), out var attributes)
                && attributes.HasFlag(FileAttributes.Directory),
            cancellationToken);

    /// <summary>
    /// Returns the object's metadata, or <see langword="null"/> when it does not exist.
    /// </summary>
    public override async Task<StoreObject?> GetObjectInfo(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        var info = await _connection.ExecuteAsync(fs =>
        {
            var target = Resolve(objectPath);
            if (!TryGetAttributes(fs, target, out var attributes))
            {
                return null;
            }

            var isFolder = attributes.HasFlag(FileAttributes.Directory);
            return ((bool IsFolder, long? Size, DateTime Modified)?)(
                isFolder,
                isFolder ? null : fs.GetFileLength(target),
                fs.GetLastWriteTimeUtc(target));
        }, cancellationToken).ConfigureAwait(false);

        if (info is null)
        {
            return null;
        }

        var (isDirectory, size, modified) = info.Value;
        return new StoreObject(
            RemotePath.ToStoragePath(RemotePath.Split(objectPath)),
            isDirectory ? StorageObjectType.Folder : StorageObjectType.File)
        {
            Size = size,
            DateModified = ToDateTimeOffset(modified)
        };
    }

    /// <inheritdoc />
    public override async Task<long> GetObjectLength(
        string fullPath,
        long defaultValue = -1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await GetObjectInfo(fullPath, cancellationToken).ConfigureAwait(false);
            return info?.Size ?? defaultValue;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Contract: this method reports a default rather than throwing.
            return defaultValue;
        }
    }

    /// <inheritdoc />
    public override async Task<Stream> OpenRead(string objectPath, CancellationToken cancellationToken = default)
    {
        var stream = await _connection.ExecuteAsync(
            fs => fs.OpenFile(Resolve(objectPath), FileMode.Open, FileAccess.Read),
            cancellationToken).ConfigureAwait(false);

        return new NfsSynchronizedStream(_connection, stream);
    }

    /// <summary>
    /// Opens a write stream. Returns <see langword="null"/> when the object exists and
    /// <paramref name="overwrite"/> is <see langword="false"/>.
    /// </summary>
    public override async Task<Stream?> OpenWrite(
        string objectPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        await CreateParentDirectories(objectPath, cancellationToken).ConfigureAwait(false);

        var stream = await _connection.ExecuteAsync(fs =>
        {
            var target = Resolve(objectPath);
            if (!overwrite && TryGetAttributes(fs, target, out _))
            {
                return null;
            }

            return fs.OpenFile(target, FileMode.Create, FileAccess.Write);
        }, cancellationToken).ConfigureAwait(false);

        return stream is null ? null : new NfsSynchronizedStream(_connection, stream);
    }

    /// <inheritdoc />
    public override async Task<Stream> OpenRange(
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var stream = await OpenRead(path, cancellationToken).ConfigureAwait(false);
        return new RangeStream(stream, offset, length);
    }

    /// <inheritdoc />
    public override async Task SetObject(
        string objectPath,
        Stream dataStream,
        string? contentType,
        bool append,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataStream);

        if (!append)
        {
            var target = await OpenWrite(objectPath, overwrite: true, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"Unable to open '{objectPath}' for writing.");
            await using (target.ConfigureAwait(false))
            {
                await dataStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await CreateParentDirectories(objectPath, cancellationToken).ConfigureAwait(false);

        var appendStream = await _connection.ExecuteAsync(fs =>
        {
            var target = Resolve(objectPath);
            // FileMode.Append only resolves an existing file, so a missing one is created first.
            var mode = TryGetAttributes(fs, target, out _) ? FileMode.Append : FileMode.Create;
            return fs.OpenFile(target, mode, FileAccess.Write);
        }, cancellationToken).ConfigureAwait(false);

        var stream = new NfsSynchronizedStream(_connection, appendStream);
        await using (stream.ConfigureAwait(false))
        {
            await dataStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a file, or a folder and everything beneath it.
    /// </summary>
    public override async Task DeleteObject(string objectPath, CancellationToken cancellationToken = default)
    {
        if (await DirectoryExists(objectPath, cancellationToken).ConfigureAwait(false))
        {
            await DeleteDirectory(objectPath, recursive: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.ExecuteAsync(object? (fs) =>
        {
            var target = Resolve(objectPath);
            if (TryGetAttributes(fs, target, out _))
            {
                fs.DeleteFile(target);
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task DeleteDirectory(
        string folderPath,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (recursive)
        {
            // NFS refuses RMDIR on a non-empty directory, so the tree is cleared depth-first.
            var children = await ListPath(folderPath, new StorageListOptions(), cancellationToken)
                .ConfigureAwait(false);

            foreach (var child in children)
            {
                if (child.IsFolder)
                {
                    await DeleteDirectory(child.FullPath, recursive: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DeleteObject(child.FullPath, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (RemotePath.Split(Resolve(folderPath)).Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the root of the NFS export.");
        }

        await _connection.ExecuteAsync(object? (fs) =>
        {
            var target = Resolve(folderPath);
            if (TryGetAttributes(fs, target, out var attributes) && attributes.HasFlag(FileAttributes.Directory))
            {
                fs.DeleteDirectory(target);
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task CreateDirectory(
        string folderPath,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var segments = RemotePath.Split(RemotePath.ApplyBasePath(_basePath, folderPath));
        if (segments.Length == 0)
        {
            return;
        }

        await _connection.ExecuteAsync(object? (fs) =>
        {
            // MKDIR creates one level at a time; walk down so intermediate folders exist.
            for (var depth = 1; depth <= segments.Length; depth++)
            {
                var level = RemotePath.ToStoragePath(segments[..depth]);
                if (TryGetAttributes(fs, level, out _))
                {
                    if (depth == segments.Length && !force)
                    {
                        throw new IOException($"NFS folder already exists: {folderPath}");
                    }

                    continue;
                }

                fs.CreateDirectory(level);
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<bool> MoveObject(
        string oldPath,
        string newPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        await CreateParentDirectories(newPath, cancellationToken).ConfigureAwait(false);

        return await _connection.ExecuteAsync(fs =>
        {
            var source = Resolve(oldPath);
            var destination = Resolve(newPath);

            if (!TryGetAttributes(fs, source, out var attributes))
            {
                return false;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (TryGetAttributes(fs, destination, out _))
                {
                    return false;
                }

                fs.MoveDirectory(source, destination);
                return true;
            }

            if (!overwrite && TryGetAttributes(fs, destination, out _))
            {
                return false;
            }

            fs.MoveFile(source, destination, overwrite);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task MoveDirectory(
        string sourceFolderPath,
        string destinationFolderPath,
        CancellationToken cancellationToken = default)
    {
        await CreateParentDirectories(destinationFolderPath, cancellationToken).ConfigureAwait(false);
        await _connection.ExecuteAsync(object? (fs) =>
        {
            fs.MoveDirectory(Resolve(sourceFolderPath), Resolve(destinationFolderPath));
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the folders leading up to <paramref name="objectPath"/>, so writing to a
    /// nested path behaves like it does on the other FluentStorage providers.
    /// </summary>
    private async Task CreateParentDirectories(string objectPath, CancellationToken cancellationToken)
    {
        var (parent, _) = RemotePath.SplitLeaf(objectPath);
        if (parent.Length > 0)
        {
            await CreateDirectory(RemotePath.ToStoragePath(parent), force: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a listing failure is the server refusing READDIRPLUS, which some NFS servers
    /// disable. The status arrives either raw or wrapped in an <see cref="IOException"/>,
    /// depending on where in the enumeration it surfaces.
    /// </summary>
    private static bool IsReadDirPlusUnsupported(Exception ex)
        => ex is Nfs3Exception { NfsStatus: Nfs3Status.NotSupported }
            || ex.InnerException is Nfs3Exception { NfsStatus: Nfs3Status.NotSupported };

    /// <summary>
    /// Stats a path, reporting absence as <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The library's own <c>FileExists</c> and <c>DirectoryExists</c> throw for missing
    /// paths instead of returning false, so existence checks go through this.
    /// </remarks>
    private static bool TryGetAttributes(NfsFileSystem fileSystem, string path, out FileAttributes attributes)
    {
        try
        {
            attributes = fileSystem.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = FileAttributes.None;
            return false;
        }
        catch (IOException ex) when (ex.InnerException is Nfs3Exception
        {
            NfsStatus: Nfs3Status.NoSuchEntity or Nfs3Status.NotDirectory or Nfs3Status.StaleFileHandle
        })
        {
            // NfsFileSystem wraps protocol errors in a plain IOException.
            attributes = FileAttributes.None;
            return false;
        }
    }

    /// <summary>
    /// Resolves a caller-supplied object path against the configured base path.
    /// </summary>
    private string Resolve(string? path) => RemotePath.ApplyBasePath(_basePath, path);

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value)
        => value is null || value.Value == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    /// <inheritdoc />
    public override void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
