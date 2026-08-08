using FluentStorage.Enums;
using FluentStorage.Model;
using FluentStorage.Storage;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.SMB1;
using StorageExtensions.Internal;
using SmbFileAttributes = SMBLibrary.FileAttributes;

namespace StorageExtensions.Smb;

/// <summary>
/// A FluentStorage store backed by an SMB/CIFS share reached directly over the network.
/// Nothing is mounted by the operating system, so the process needs no root privileges
/// and no host-level configuration.
/// </summary>
public class SmbStore : StoreBase
{
    private const int NonDirectory = (int)CreateOptions.FILE_NON_DIRECTORY_FILE;
    private const int Directory = (int)CreateOptions.FILE_DIRECTORY_FILE;

    /// <summary>
    /// Opens whatever the path refers to. FILE_DIRECTORY_FILE and FILE_NON_DIRECTORY_FILE
    /// are mutually exclusive — combining them is rejected with STATUS_INVALID_PARAMETER —
    /// so "either kind" is expressed by asking for neither.
    /// </summary>
    private const int AnyType = 0;

    private readonly SmbConnection _connection;
    private readonly string? _basePath;

    /// <summary>
    /// Creates a store for the share described by <paramref name="options"/>. The connection
    /// is established lazily on first use.
    /// </summary>
    public SmbStore(SmbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("SMB storage requires a host.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Share))
        {
            throw new ArgumentException("SMB storage requires a share name.", nameof(options));
        }

        _connection = new SmbConnection(options);
        _basePath = options.BasePath;
    }

    /// <summary>An SMB share is a real file system, not an object bucket.</summary>
    public override Task<bool> IsFileSystem() => Task.FromResult(true);

    /// <summary>SMB reads carry an explicit offset, so random access is native.</summary>
    public override Task<bool> IsSeekable() => Task.FromResult(true);

    /// <summary>Returns the connected <see cref="ISMBFileStore"/> for the share.</summary>
    public override Task<object> GetClient()
        => _connection.ExecuteAsync(object (session) => session.Tree);

    /// <summary>
    /// Lists one folder. Recursion, filtering, and result limits are applied by
    /// <see cref="StoreBase"/> on top of this.
    /// </summary>
    protected override async Task<List<StoreObject>> ListPath(
        string path,
        StorageListOptions options,
        CancellationToken cancellationToken = default)
    {
        var smbFolder = Resolve(path);

        var entries = await _connection.ExecuteAsync(
            session => session.Tree is SMB1FileStore smb1
                ? ListEntriesCifs(smb1, smbFolder)
                : ListEntriesSmb2(session, smbFolder),
            cancellationToken).ConfigureAwait(false);

        var result = new List<StoreObject>();
        foreach (var entry in entries)
        {
            var item = new StoreObject(
                RemotePath.CombineStoragePath(path, entry.Name),
                entry.IsFolder ? StorageObjectType.Folder : StorageObjectType.File)
            {
                Size = entry.IsFolder ? null : entry.Size,
                DateCreated = ToDateTimeOffset(entry.Created),
                DateModified = ToDateTimeOffset(entry.Modified)
            };

            if (options.IsMatch(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// One directory entry, normalised across the two dialects' very different
    /// enumeration APIs.
    /// </summary>
    private readonly record struct DirectoryEntry(
        string Name,
        bool IsFolder,
        long Size,
        DateTime? Created,
        DateTime? Modified);

    /// <summary>
    /// SMB2/3 enumeration: open the directory, then page through it by handle.
    /// </summary>
    private static List<DirectoryEntry> ListEntriesSmb2(SmbSession session, string smbFolder)
    {
        var status = session.Tree.CreateFile(
            out var handle,
            out _,
            smbFolder,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbFileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            (CreateOptions)(Directory | (int)CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT),
            null);

        if (status.IsNotFound())
        {
            return [];
        }

        status.EnsureSuccess($"open folder '{smbFolder}'");

        try
        {
            var queryStatus = session.Tree.QueryDirectory(
                out var files, handle, "*", FileInformationClass.FileDirectoryInformation);

            // QueryDirectory pages internally and reports STATUS_NO_MORE_FILES once the
            // enumeration is exhausted — that is the success path, and the entries it
            // gathered are already in `files`.
            if (queryStatus is not NTStatus.STATUS_SUCCESS and not NTStatus.STATUS_NO_MORE_FILES)
            {
                queryStatus.EnsureSuccess($"list folder '{smbFolder}'");
            }

            var entries = new List<DirectoryEntry>();
            foreach (var file in (files ?? []).OfType<FileDirectoryInformation>())
            {
                if (file.FileName is "." or "..")
                {
                    continue;
                }

                entries.Add(new DirectoryEntry(
                    file.FileName,
                    file.FileAttributes.HasFlag(SmbFileAttributes.Directory),
                    file.EndOfFile,
                    file.CreationTime,
                    file.LastWriteTime));
            }

            return entries;
        }
        finally
        {
            session.Tree.CloseFile(handle);
        }
    }

    /// <summary>
    /// SMB1 enumeration. CIFS has no handle-based directory query — SMBLibrary leaves that
    /// overload unimplemented — so listing goes through TRANS2_FIND_FIRST2 with a wildcard
    /// path instead.
    /// </summary>
    private static List<DirectoryEntry> ListEntriesCifs(SMB1FileStore tree, string smbFolder)
    {
        var pattern = smbFolder.Length == 0 ? @"\*" : $@"\{smbFolder}\*";
        var status = tree.QueryDirectory(out var found, pattern, FindInformationLevel.SMB_FIND_FILE_BOTH_DIRECTORY_INFO);

        if (status.IsNotFound())
        {
            return [];
        }

        status.EnsureSuccess($"list folder '{smbFolder}'");

        var entries = new List<DirectoryEntry>();
        foreach (var file in (found ?? []).OfType<FindFileBothDirectoryInfo>())
        {
            if (file.FileName is "." or "..")
            {
                continue;
            }

            entries.Add(new DirectoryEntry(
                file.FileName,
                file.ExtFileAttributes.HasFlag(ExtendedFileAttributes.Directory),
                file.EndOfFile,
                file.CreationTime,
                file.LastWriteTime));
        }

        return entries;
    }

    /// <inheritdoc />
    public override Task<bool> ObjectExists(string objectPath, CancellationToken cancellationToken = default)
        => ExistsAsync(objectPath, AnyType, cancellationToken);

    /// <inheritdoc />
    public override Task<bool> DirectoryExists(string folderPath, CancellationToken cancellationToken = default)
        => ExistsAsync(folderPath, Directory, cancellationToken);

    /// <summary>
    /// Returns the object's metadata, or <see langword="null"/> when it does not exist.
    /// </summary>
    public override async Task<StoreObject?> GetObjectInfo(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        var smbPath = Resolve(objectPath);

        var info = await _connection.ExecuteAsync(
            (FileBasicInformation Basic, FileStandardInformation Standard)? (session) =>
        {
            var status = OpenPath(
                session, smbPath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                CreateDisposition.FILE_OPEN, AnyType, out var handle);

            if (status.IsNotFound())
            {
                return null;
            }

            status.EnsureSuccess($"open '{smbPath}'");

            try
            {
                session.Tree.GetFileInformation(out var basic, handle, FileInformationClass.FileBasicInformation)
                    .EnsureSuccess($"query '{smbPath}'");
                session.Tree.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation)
                    .EnsureSuccess($"query '{smbPath}'");

                return ((FileBasicInformation)basic, (FileStandardInformation)standard);
            }
            finally
            {
                session.Tree.CloseFile(handle);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (info is null)
        {
            return null;
        }

        var (basicInfo, standardInfo) = info.Value;
        return new StoreObject(
            RemotePath.ToStoragePath(RemotePath.Split(objectPath)),
            standardInfo.Directory ? StorageObjectType.Folder : StorageObjectType.File)
        {
            Size = standardInfo.Directory ? null : standardInfo.EndOfFile,
            DateCreated = ToDateTimeOffset(basicInfo.CreationTime.Time),
            DateModified = ToDateTimeOffset(basicInfo.LastWriteTime.Time)
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
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Contract: this method reports a default rather than throwing.
            return defaultValue;
        }
    }

    /// <inheritdoc />
    public override async Task<Stream> OpenRead(string objectPath, CancellationToken cancellationToken = default)
    {
        var smbPath = Resolve(objectPath);

        var opened = await _connection.ExecuteAsync((object Handle, long Length)? (session) =>
        {
            var status = OpenPath(
                session, smbPath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                CreateDisposition.FILE_OPEN, NonDirectory, out var handle);

            if (status.IsNotFound())
            {
                return null;
            }

            status.EnsureSuccess($"open '{smbPath}' for reading");

            try
            {
                session.Tree.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation)
                    .EnsureSuccess($"query '{smbPath}'");
                return (handle, ((FileStandardInformation)standard).EndOfFile);
            }
            catch
            {
                session.Tree.CloseFile(handle);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (opened is null)
        {
            throw new FileNotFoundException($"SMB object not found: {objectPath}", objectPath);
        }

        var (fileHandle, length) = opened.Value;
        return new SmbFileStream(_connection, fileHandle, length, canRead: true, canWrite: false);
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
        var smbPath = Resolve(objectPath);
        await CreateParentDirectories(objectPath, cancellationToken).ConfigureAwait(false);

        var handle = await _connection.ExecuteAsync(session =>
        {
            var status = OpenPath(
                session, smbPath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                overwrite ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_CREATE,
                NonDirectory, out var opened);

            if (!overwrite && status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
            {
                return null;
            }

            status.EnsureSuccess($"open '{smbPath}' for writing");
            return opened;
        }, cancellationToken).ConfigureAwait(false);

        return handle is null
            ? null
            : new SmbFileStream(_connection, handle, length: 0, canRead: false, canWrite: true);
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

        var smbPath = Resolve(objectPath);
        await CreateParentDirectories(objectPath, cancellationToken).ConfigureAwait(false);

        var opened = await _connection.ExecuteAsync(session =>
        {
            var status = OpenPath(
                session, smbPath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                CreateDisposition.FILE_OPEN_IF, NonDirectory, out var handle);
            status.EnsureSuccess($"open '{smbPath}' for appending");

            try
            {
                session.Tree.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation)
                    .EnsureSuccess($"query '{smbPath}'");
                return (handle, ((FileStandardInformation)standard).EndOfFile);
            }
            catch
            {
                session.Tree.CloseFile(handle);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        var (appendHandle, existingLength) = opened;
        var stream = new SmbFileStream(_connection, appendHandle, existingLength, canRead: false, canWrite: true);
        await using (stream.ConfigureAwait(false))
        {
            stream.Seek(0, SeekOrigin.End);
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

        await DeleteEntry(objectPath, NonDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task DeleteDirectory(
        string folderPath,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (recursive)
        {
            // SMB refuses to remove a directory that still has entries, so the tree is
            // cleared depth-first before the folder itself goes.
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
                    await DeleteEntry(child.FullPath, NonDirectory, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await DeleteEntry(folderPath, Directory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task CreateDirectory(
        string folderPath,
        bool force,
        CancellationToken cancellationToken = default)
    {
        var segments = RemotePath.Split(folderPath);
        if (segments.Length == 0)
        {
            return;
        }

        // SMB creates one level at a time; walk down so intermediate folders exist.
        for (var depth = 1; depth <= segments.Length; depth++)
        {
            var smbPath = Resolve(RemotePath.ToStoragePath(segments[..depth]));
            var isLeaf = depth == segments.Length;

            await _connection.ExecuteAsync(object? (session) =>
            {
                var status = OpenPath(
                    session, smbPath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                    CreateDisposition.FILE_OPEN_IF, Directory, out var handle);

                if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                {
                    if (isLeaf && !force)
                    {
                        throw new IOException($"SMB folder already exists: {folderPath}");
                    }

                    return null;
                }

                status.EnsureSuccess($"create folder '{smbPath}'");
                session.Tree.CloseFile(handle);
                return null;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task<bool> MoveObject(
        string oldPath,
        string newPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
        => Rename(oldPath, newPath, overwrite, AnyType, cancellationToken);

    /// <inheritdoc />
    public override async Task MoveDirectory(
        string sourceFolderPath,
        string destinationFolderPath,
        CancellationToken cancellationToken = default)
        => await Rename(sourceFolderPath, destinationFolderPath, overwrite: false, Directory, cancellationToken)
            .ConfigureAwait(false);

    private enum RenameOutcome
    {
        Renamed,
        SourceMissing,
        DestinationExists,

        /// <summary>The server will not perform a rename over this dialect.</summary>
        Unsupported
    }

    private async Task<bool> Rename(
        string oldPath,
        string newPath,
        bool overwrite,
        int createOptions,
        CancellationToken cancellationToken)
    {
        var source = Resolve(oldPath);
        // FileRenameInformation takes a path relative to the share, matching CreateFile.
        var destination = Resolve(newPath);

        if (!overwrite && await ObjectExists(newPath, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await CreateParentDirectories(newPath, cancellationToken).ConfigureAwait(false);

        var outcome = await _connection.ExecuteAsync(session =>
        {
            var status = OpenPath(
                session, source, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                CreateDisposition.FILE_OPEN, createOptions, out var handle);

            if (status.IsNotFound())
            {
                return RenameOutcome.SourceMissing;
            }

            status.EnsureSuccess($"open '{source}' for rename");

            try
            {
                var rename = new FileRenameInformationType2
                {
                    FileName = destination,
                    ReplaceIfExists = overwrite
                };

                var renameStatus = session.Tree.SetFileInformation(handle, rename);
                return renameStatus switch
                {
                    NTStatus.STATUS_OBJECT_NAME_COLLISION when !overwrite => RenameOutcome.DestinationExists,
                    NTStatus.STATUS_NOT_SUPPORTED => RenameOutcome.Unsupported,
                    _ => Renamed(renameStatus)
                };

                RenameOutcome Renamed(NTStatus s)
                {
                    s.EnsureSuccess($"rename '{source}' to '{destination}'");
                    return RenameOutcome.Renamed;
                }
            }
            finally
            {
                session.Tree.CloseFile(handle);
            }
        }, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            RenameOutcome.Renamed => true,
            RenameOutcome.Unsupported => await CopyThenDelete(oldPath, newPath, cancellationToken)
                .ConfigureAwait(false),
            _ => false
        };
    }

    /// <summary>
    /// Move fallback for servers that reject a handle-based rename, which in practice means
    /// SMB1 servers that do not advertise info-level passthrough.
    /// </summary>
    /// <remarks>
    /// Copying and then deleting is not atomic: an interrupted move can leave the source in
    /// place alongside a partial destination. It is the only move such servers offer.
    /// </remarks>
    private async Task<bool> CopyThenDelete(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        if (await DirectoryExists(oldPath, cancellationToken).ConfigureAwait(false))
        {
            await CreateDirectory(newPath, force: true, cancellationToken).ConfigureAwait(false);

            foreach (var child in await ListPath(oldPath, new StorageListOptions(), cancellationToken)
                .ConfigureAwait(false))
            {
                await CopyThenDelete(
                    child.FullPath,
                    RemotePath.CombineStoragePath(newPath, child.Name),
                    cancellationToken).ConfigureAwait(false);
            }

            await DeleteDirectory(oldPath, recursive: true, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!await ObjectExists(oldPath, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var source = await OpenRead(oldPath, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            await SetObject(newPath, source, contentType: null, append: false, cancellationToken)
                .ConfigureAwait(false);
        }

        await DeleteObject(oldPath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DeleteEntry(string path, int createOptions, CancellationToken cancellationToken)
    {
        var smbPath = Resolve(path);
        if (string.IsNullOrEmpty(smbPath))
        {
            throw new InvalidOperationException("Refusing to delete the root of the SMB share.");
        }

        await _connection.ExecuteAsync(object? (session) =>
        {
            var status = OpenPath(
                session, smbPath, AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                CreateDisposition.FILE_OPEN, createOptions | (int)CreateOptions.FILE_DELETE_ON_CLOSE,
                out var handle);

            if (status.IsNotFound())
            {
                return null;
            }

            status.EnsureSuccess($"open '{smbPath}' for delete");

            try
            {
                // FILE_DELETE_ON_CLOSE covers most servers; setting the disposition
                // explicitly also satisfies those that ignore the create option.
                session.Tree.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            }
            finally
            {
                session.Tree.CloseFile(handle);
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExistsAsync(string path, int createOptions, CancellationToken cancellationToken)
    {
        var smbPath = Resolve(path);
        if (string.IsNullOrEmpty(smbPath))
        {
            // The share root is a directory and always exists once tree connect succeeded,
            // which satisfies both the object and the directory form of this question.
            return true;
        }

        return await _connection.ExecuteAsync(session =>
        {
            var status = OpenPath(
                session, smbPath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                CreateDisposition.FILE_OPEN, createOptions, out var handle);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                return false;
            }

            session.Tree.CloseFile(handle);
            return true;
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

    private static NTStatus OpenPath(
        SmbSession session,
        string smbPath,
        AccessMask access,
        CreateDisposition disposition,
        int createOptions,
        out object handle)
        => session.Tree.CreateFile(
            out handle,
            out _,
            smbPath,
            access,
            SmbFileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            disposition,
            (CreateOptions)createOptions,
            null);

    private string Resolve(string? path) => RemotePath.ToSmb(RemotePath.ApplyBasePath(_basePath, path));

    /// <summary>
    /// SMB reports "no timestamp" as zero, which <see cref="DateTime"/> would otherwise
    /// render as year 1601.
    /// </summary>
    private static DateTimeOffset? ToDateTimeOffset(DateTime value)
        => value == default ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value)
        => value is null ? null : ToDateTimeOffset(value.Value);

    /// <inheritdoc />
    public override void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
