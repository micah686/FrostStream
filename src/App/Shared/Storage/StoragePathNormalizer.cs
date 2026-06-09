namespace Shared.Storage;

public static class StoragePathNormalizer
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        Span<char> buffer = path.Length <= 4096
            ? stackalloc char[path.Length]
            : new char[path.Length];

        var previousWasSlash = false;
        var length = 0;
        foreach (var ch in path)
        {
            var normalized = ch == '\\' ? '/' : ch;
            if (normalized == '/')
            {
                if (previousWasSlash)
                {
                    continue;
                }

                previousWasSlash = true;
            }
            else
            {
                previousWasSlash = false;
            }

            buffer[length++] = normalized;
        }

        var start = 0;
        while (start < length && buffer[start] == '/')
        {
            start++;
        }

        var end = length - 1;
        while (end >= start && buffer[end] == '/')
        {
            end--;
        }

        if (end < start)
        {
            return "/";
        }

        return "/" + new string(buffer[start..(end + 1)]);
    }
}
