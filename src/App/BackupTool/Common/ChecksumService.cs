using System.Security.Cryptography;

namespace BackupTool;

internal sealed record ChecksumEntry(string RelativePath, string Hash);

internal static class ChecksumService
{
    public const string FileName = "checksums.sha256";

    public static async Task<IReadOnlyList<ChecksumEntry>> ComputeAsync(
        string archive,
        bool includeChecksumFile = true)
    {
        var entries = new List<ChecksumEntry>();
        foreach (var file in Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(archive, file).Replace('\\', '/');
            if (!includeChecksumFile && string.Equals(relative, FileName, StringComparison.Ordinal))
                continue;

            entries.Add(new ChecksumEntry(relative, await HashFileAsync(file)));
        }

        return entries;
    }

    public static Task WriteAsync(string archive, IReadOnlyList<ChecksumEntry> entries)
        => File.WriteAllLinesAsync(
            Path.Combine(archive, FileName),
            entries.Select(x => $"{x.Hash}  {x.RelativePath}"));

    public static async Task<IReadOnlyList<ChecksumEntry>> ReadAsync(string archive)
    {
        var checksumPath = Path.Combine(archive, FileName);
        if (!File.Exists(checksumPath))
            throw new FileNotFoundException("Backup checksum file was not found.", checksumPath);

        var entries = new List<ChecksumEntry>();
        foreach (var line in await File.ReadAllLinesAsync(checksumPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2)
                throw new InvalidOperationException($"Invalid checksum line: {line}");
            entries.Add(new ChecksumEntry(parts[1], parts[0]));
        }

        return entries;
    }

    public static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}
