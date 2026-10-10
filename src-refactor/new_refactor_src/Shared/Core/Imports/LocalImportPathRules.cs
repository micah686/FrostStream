namespace Shared.Imports;

public static class LocalImportPathRules
{
    public static bool TryNormalizeRelativePath(string? path, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.StartsWith("/", StringComparison.Ordinal) || trimmed.StartsWith("\\", StringComparison.Ordinal))
        {
            error = "Path must be relative.";
            return false;
        }

        if (trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
        {
            error = "Path must not include a drive root.";
            return false;
        }

        var segments = trimmed
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            error = "Path is required.";
            return false;
        }

        if (segments.Any(segment => segment is "." or ".."))
        {
            error = "Path must not contain traversal segments.";
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    public static bool TryResolveUnderAllowedRoots(
        string? sourceRoot,
        string? relativePath,
        IReadOnlyList<string> allowedRoots,
        out string fullPath,
        out string normalizedRelativePath,
        out string error)
    {
        fullPath = string.Empty;
        normalizedRelativePath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            error = "sourceRoot is required.";
            return false;
        }

        if (allowedRoots.Count == 0)
        {
            error = "No local import roots are configured for this worker.";
            return false;
        }

        if (!TryNormalizeRelativePath(relativePath, out normalizedRelativePath, out error))
            return false;

        if (!Path.IsPathRooted(sourceRoot))
        {
            error = "sourceRoot must be an absolute path.";
            return false;
        }

        var rootFullPath = Path.GetFullPath(sourceRoot);
        var resolvedFullPath = Path.GetFullPath(Path.Combine(rootFullPath, normalizedRelativePath));
        fullPath = resolvedFullPath;
        if (!IsWithinDirectoryOrSame(resolvedFullPath, rootFullPath))
        {
            error = "Resolved path is outside sourceRoot.";
            return false;
        }

        var normalizedAllowedRoots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Where(Path.IsPathRooted)
            .Select(Path.GetFullPath)
            .ToArray();

        if (normalizedAllowedRoots.Length == 0)
        {
            error = "No absolute local import roots are configured for this worker.";
            return false;
        }

        if (!normalizedAllowedRoots.Any(root => IsWithinDirectoryOrSame(resolvedFullPath, root)))
        {
            error = "Resolved path is outside configured allowed import roots.";
            return false;
        }

        return true;
    }

    public static bool IsWithinDirectoryOrSame(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "."
               || (!relative.StartsWith("..", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative));
    }
}
