using FluentStorage;

namespace Shared.Storage;

/// <summary>FluentStorage-compatible helpers for backend-relative object paths.</summary>
public static class StorageObjectPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectTraversal(path);
        return StoragePath.Normalize(path.Replace('\\', '/'));
    }

    public static string Combine(params string?[] parts)
    {
        foreach (var part in parts.Where(part => !string.IsNullOrWhiteSpace(part)))
        {
            RejectTraversal(part!);
        }

        return StoragePath.Combine(parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => StoragePath.Normalize(part!.Replace('\\', '/'))));
    }

    public static string GetParent(string path)
        => StoragePath.GetParent(Normalize(path));

    private static void RejectTraversal(string path)
    {
        var components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".."))
        {
            throw new ArgumentException("Storage object paths cannot contain traversal segments.", nameof(path));
        }
    }
}
