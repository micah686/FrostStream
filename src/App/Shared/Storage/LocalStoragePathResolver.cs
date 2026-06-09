namespace Shared.Storage;

public static class LocalStoragePathResolver
{
    public const string EnvironmentVariableName = "FROSTSTREAM_STORAGE_ROOT";
    public const string StorageRootToken = "${FROSTSTREAM_STORAGE_ROOT}";

    public static string Resolve(string path)
    {
        return Resolve(path, Environment.GetEnvironmentVariable(EnvironmentVariableName));
    }

    public static string Resolve(string path, string? storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!path.Contains(StorageRootToken, StringComparison.Ordinal))
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new InvalidOperationException(
                $"Local storage path '{path}' requires the {EnvironmentVariableName} environment variable.");
        }

        if (!Path.IsPathRooted(storageRoot))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariableName} must be an absolute path, but was '{storageRoot}'.");
        }

        var absoluteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
        return path.Replace(StorageRootToken, absoluteRoot, StringComparison.Ordinal);
    }
}
