using DiscUtils.Nfs;

namespace StorageExtensions.Nfs;

/// <summary>Small adapter surface around DiscUtils so store behavior can be unit tested.</summary>
internal interface INfsFileSystem : IDisposable
{
    object Client { get; }
    FileAttributes GetAttributes(string path);
    IEnumerable<string> GetFileSystemEntries(string path);
    long GetFileLength(string path);
    DateTime GetLastWriteTimeUtc(string path);
    Stream OpenFile(string path, FileMode mode, FileAccess access);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void CreateDirectory(string path);
    void MoveDirectory(string source, string destination);
    void MoveFile(string source, string destination, bool overwrite);
}

internal sealed class DiscUtilsNfsFileSystem(NfsFileSystem fileSystem) : INfsFileSystem
{
    public object Client => fileSystem;
    public FileAttributes GetAttributes(string path) => fileSystem.GetAttributes(path);
    public IEnumerable<string> GetFileSystemEntries(string path) => fileSystem.GetFileSystemEntries(path);
    public long GetFileLength(string path) => fileSystem.GetFileLength(path);
    public DateTime GetLastWriteTimeUtc(string path) => fileSystem.GetLastWriteTimeUtc(path);
    public Stream OpenFile(string path, FileMode mode, FileAccess access) => fileSystem.OpenFile(path, mode, access);
    public void DeleteFile(string path) => fileSystem.DeleteFile(path);
    public void DeleteDirectory(string path) => fileSystem.DeleteDirectory(path);
    public void CreateDirectory(string path) => fileSystem.CreateDirectory(path);
    public void MoveDirectory(string source, string destination) => fileSystem.MoveDirectory(source, destination);
    public void MoveFile(string source, string destination, bool overwrite) => fileSystem.MoveFile(source, destination, overwrite);
    public void Dispose() => fileSystem.Dispose();
}
