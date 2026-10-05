using FluentStorage;

namespace StorageExtensions.Internal;

/// <summary>
/// Translates FluentStorage object paths into the share-relative form the SMB and NFS
/// clients expect. FluentStorage paths are '/'-separated and may or may not be rooted;
/// both wire protocols address files relative to the share/export root instead.
/// </summary>
internal static class RemotePath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Splits an object path into share-relative segments. The root path yields an empty array.
    /// </summary>
    /// <exception cref="ArgumentException">The path attempts to escape the share root.</exception>
    public static string[] Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var raw = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<string>(raw.Length);
        foreach (var segment in raw)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == StoragePath.LevelUpFolderName)
            {
                // Resolving '..' locally would let a caller address files outside the share,
                // which neither protocol scopes for us.
                throw new ArgumentException($"Path '{path}' escapes the share root.", nameof(path));
            }

            segments.Add(segment);
        }

        return segments.ToArray();
    }

    /// <summary>
    /// Renders an object path in the backslash-separated, share-relative form SMB uses.
    /// The share root is the empty string.
    /// </summary>
    public static string ToSmb(string? path) => string.Join('\\', Split(path));

    /// <summary>
    /// Renders share-relative segments back into a canonical FluentStorage path.
    /// </summary>
    public static string ToStoragePath(IEnumerable<string> segments)
        => StoragePath.Normalize(string.Join(StoragePath.PathSeparator, segments));

    /// <summary>
    /// Combines a folder path with a child name, producing a canonical FluentStorage path.
    /// </summary>
    public static string CombineStoragePath(string? folderPath, string name)
        => ToStoragePath([.. Split(folderPath), name]);

    /// <summary>
    /// Splits an object path into its parent segments and its leaf name.
    /// </summary>
    /// <exception cref="ArgumentException">The path refers to the share root, which has no leaf.</exception>
    public static (string[] Parent, string Name) SplitLeaf(string? path)
    {
        var segments = Split(path);
        if (segments.Length == 0)
        {
            throw new ArgumentException("The share root does not name a file or folder.", nameof(path));
        }

        return (segments[..^1], segments[^1]);
    }

    /// <summary>
    /// Prefixes an object path with the store's base path, if one is configured.
    /// </summary>
    public static string ApplyBasePath(string? basePath, string? path)
        => string.IsNullOrWhiteSpace(basePath)
            ? ToStoragePath(Split(path))
            : ToStoragePath([.. Split(basePath), .. Split(path)]);
}
