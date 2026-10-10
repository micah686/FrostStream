namespace YtDlpSharpLib.IntegrationTests;

internal sealed class TemporaryWorkspace : IAsyncDisposable
{
    private static readonly HashSet<string> IgnoredMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".description",
        ".json",
        ".part",
        ".temp",
        ".tmp",
        ".ytdl"
    };

    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private TemporaryWorkspace(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryWorkspace Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "YtDlpSharpLib.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryWorkspace(path);
    }

    public async Task<string> SingleFileAsync(string searchPattern)
    {
        var files = Directory.GetFiles(Path, searchPattern, SearchOption.TopDirectoryOnly);
        await Assert.That(files).Count().IsEqualTo(1);
        return files[0];
    }

    public async Task<string> SingleMediaFileAsync()
    {
        var files = MediaFiles();
        await Assert.That(files).Count().IsEqualTo(1);
        return files[0];
    }

    public string[] MediaFiles() =>
        Directory.GetFiles(Path, "*", SearchOption.TopDirectoryOnly)
            .Where(file => !IgnoredMediaExtensions.Contains(System.IO.Path.GetExtension(file)))
            .Where(file => !file.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase))
            .Where(file => !ThumbnailExtensions.Contains(System.IO.Path.GetExtension(file)))
            .ToArray();

    public string[] ThumbnailFiles() =>
        Directory.GetFiles(Path, "*", SearchOption.TopDirectoryOnly)
            .Where(file => ThumbnailExtensions.Contains(System.IO.Path.GetExtension(file)))
            .ToArray();

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
