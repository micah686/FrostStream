using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Options;
using YtDlpSharpLib.Exceptions;

namespace YtDlpSharpLib.Provisioning;

/// <summary>
/// Default <see cref="IYtDlpBinaryDownloader"/>. Resolves the correct release asset for the
/// current OS/architecture, streams the download with progress reporting, atomically writes the
/// final file (<c>*.download</c> staging file + <see cref="File.Move(string, string, bool)"/>),
/// and applies a Unix executable bit on non-Windows platforms.
/// </summary>
public sealed class YtDlpBinaryDownloader : IYtDlpBinaryDownloader, IDisposable
{
    private const int CopyBufferSize = 81920;

    private readonly YtDlpBinaryDownloaderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>Creates a downloader from typed options. Suitable for direct (non-DI) usage.</summary>
    /// <param name="options">Configuration; must not be null.</param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> to use for outbound requests. When <see langword="null"/>
    /// the downloader creates and owns one, applying <see cref="YtDlpBinaryDownloaderOptions.HttpTimeout"/>
    /// and <see cref="YtDlpBinaryDownloaderOptions.UserAgent"/>. Externally-supplied clients are not
    /// disposed by this instance.
    /// </param>
    public YtDlpBinaryDownloader(YtDlpBinaryDownloaderOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = options.HttpTimeout
            };
            if (!string.IsNullOrWhiteSpace(options.UserAgent))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            }

            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>Creates a downloader from <see cref="IOptions{TOptions}"/>. Used by the DI container.</summary>
    public YtDlpBinaryDownloader(IOptions<YtDlpBinaryDownloaderOptions> options, HttpClient httpClient)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, httpClient)
    {
    }

    /// <inheritdoc />
    public async Task<string> DownloadYtDlpAsync(
        string? directory = null,
        IProgress<BinaryDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var url = ResolveYtDlpUrl();
        var dir = ResolveDirectory(directory);
        var fileName = ExtractFileNameFromUrl(url);
        var destination = Path.Combine(dir, fileName);

        await DownloadFileAsync(url, destination, BinaryKind.YtDlp, progress, ct).ConfigureAwait(false);
        ApplyUnixExecBit(destination);
        return destination;
    }

    /// <inheritdoc />
    public Task<string> DownloadFfmpegAsync(
        string? directory = null,
        IProgress<BinaryDownloadProgress>? progress = null,
        CancellationToken ct = default)
        => DownloadFfBinaryAsync(BinaryKind.Ffmpeg, directory, progress, ct);

    /// <inheritdoc />
    public Task<string> DownloadFfprobeAsync(
        string? directory = null,
        IProgress<BinaryDownloadProgress>? progress = null,
        CancellationToken ct = default)
        => DownloadFfBinaryAsync(BinaryKind.Ffprobe, directory, progress, ct);

    /// <inheritdoc />
    public async Task<string> DownloadDenoAsync(
        string? directory = null,
        IProgress<BinaryDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var target = ResolveDenoTarget();
        var versionUrl = _options.DenoVersionUrl;
        string version;

        try
        {
            version = (await _httpClient.GetStringAsync(versionUrl, ct).ConfigureAwait(false)).Trim();
        }
        catch (HttpRequestException ex)
        {
            throw new YtDlpBinaryDownloadException(
                "Failed to query Deno latest-version metadata.",
                versionUrl,
                ex);
        }

        if (string.IsNullOrEmpty(version))
        {
            throw new YtDlpBinaryDownloadException(
                "Deno latest-version endpoint returned an empty response.",
                versionUrl);
        }

        var downloadUrl = string.Format(
            CultureInfo.InvariantCulture,
            _options.DenoDownloadUrlTemplate,
            version,
            target);

        var dir = ResolveDirectory(directory);
        return await DownloadAndExtractZipAsync(
                downloadUrl,
                "deno",
                dir,
                BinaryKind.Deno,
                progress,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> DownloadBgUtilPluginAsync(
        string? directory = null,
        IProgress<BinaryDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var url = string.Format(
            CultureInfo.InvariantCulture,
            _options.BgUtilPluginUrlTemplate,
            _options.BgUtilPluginVersion);

        var dir = ResolveDirectory(directory);
        var pluginRoot = Path.Combine(dir, _options.BgUtilPluginDirectoryName);

        await using var memory = new MemoryStream();
        try
        {
            await DownloadToStreamAsync(url, memory, BinaryKind.BgUtilPlugin, progress, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new YtDlpBinaryDownloadException($"Failed to download the bgutil plugin from {url}.", url, ex);
        }

        memory.Position = 0;

        // Extract into a staging dir then swap into place so a half-extracted tree is never left behind
        // (and never satisfies the SkipExisting check on the next run).
        var stagingRoot = pluginRoot + ".download";
        TryDeleteDirectory(stagingRoot);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            ExtractArchive(archive, stagingRoot, url);

            TryDeleteDirectory(pluginRoot);
            Directory.Move(stagingRoot, pluginRoot);
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }

        return pluginRoot;
    }

    /// <inheritdoc />
    public async Task<BinaryDownloadResult> DownloadAllAsync(
        BinaryDownloadOptions? options = null,
        IProgress<BinaryDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var resolved = options ?? new BinaryDownloadOptions();
        var dir = ResolveDirectory(resolved.Directory);
        Directory.CreateDirectory(dir);

        string? ytDlpPath = null;
        string? ffmpegPath = null;
        string? ffprobePath = null;
        string? denoPath = null;
        string? bgUtilPluginDir = null;

        if (resolved.DownloadYtDlp)
        {
            var expected = Path.Combine(dir, ExpectedYtDlpFileName());
            ytDlpPath = (resolved.SkipExisting && File.Exists(expected))
                ? expected
                : await DownloadYtDlpAsync(dir, progress, ct).ConfigureAwait(false);
        }

        if (resolved.DownloadFfmpeg)
        {
            var expected = Path.Combine(dir, ExpectedFfBinaryFileName(BinaryKind.Ffmpeg));
            ffmpegPath = (resolved.SkipExisting && File.Exists(expected))
                ? expected
                : await DownloadFfmpegAsync(dir, progress, ct).ConfigureAwait(false);
        }

        if (resolved.DownloadFfprobe)
        {
            var expected = Path.Combine(dir, ExpectedFfBinaryFileName(BinaryKind.Ffprobe));
            ffprobePath = (resolved.SkipExisting && File.Exists(expected))
                ? expected
                : await DownloadFfprobeAsync(dir, progress, ct).ConfigureAwait(false);
        }

        if (resolved.DownloadDeno)
        {
            var expected = Path.Combine(dir, ExpectedDenoFileName());
            denoPath = (resolved.SkipExisting && File.Exists(expected))
                ? expected
                : await DownloadDenoAsync(dir, progress, ct).ConfigureAwait(false);
        }

        if (resolved.DownloadBgUtilPlugin)
        {
            var expected = Path.Combine(dir, _options.BgUtilPluginDirectoryName);
            // The extracted package tree lives under <pluginRoot>/yt_dlp_plugins; treat its presence as "already provisioned".
            bgUtilPluginDir = (resolved.SkipExisting && Directory.Exists(Path.Combine(expected, "yt_dlp_plugins")))
                ? expected
                : await DownloadBgUtilPluginAsync(dir, progress, ct).ConfigureAwait(false);
        }

        return new BinaryDownloadResult
        {
            YtDlpPath = ytDlpPath,
            FfmpegPath = ffmpegPath,
            FfprobePath = ffprobePath,
            DenoPath = denoPath,
            BgUtilPluginDir = bgUtilPluginDir
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> DownloadFfBinaryAsync(
        BinaryKind kind,
        string? directory,
        IProgress<BinaryDownloadProgress>? progress,
        CancellationToken ct)
    {
        ThrowIfDisposed();

        var urls = await GetFfBinariesUrlsAsync(ct).ConfigureAwait(false);
        var url = kind switch
        {
            BinaryKind.Ffmpeg => urls.Ffmpeg,
            BinaryKind.Ffprobe => urls.Ffprobe,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an ffbinaries kind.")
        };

        if (string.IsNullOrEmpty(url))
        {
            throw new YtDlpBinaryDownloadException(
                $"ffbinaries did not advertise a {kind} URL for the current platform.",
                _options.FfBinariesApiUrl);
        }

        var dir = ResolveDirectory(directory);
        var entryPrefix = kind == BinaryKind.Ffmpeg ? "ffmpeg" : "ffprobe";
        return await DownloadAndExtractZipAsync(url, entryPrefix, dir, kind, progress, ct).ConfigureAwait(false);
    }

    private async Task<FfBinariesUrls> GetFfBinariesUrlsAsync(CancellationToken ct)
    {
        var apiUrl = _options.FfBinariesApiUrl;
        string json;
        try
        {
            json = await _httpClient.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new YtDlpBinaryDownloadException("Failed to query the ffbinaries API.", apiUrl, ex);
        }

        FfBinariesResponse? response;
        try
        {
            response = JsonSerializer.Deserialize(json, BinaryDownloaderJsonContext.Default.FfBinariesResponse);
        }
        catch (JsonException ex)
        {
            throw new YtDlpBinaryDownloadException("Could not parse the ffbinaries API response.", apiUrl, ex);
        }

        if (response?.Bin is null)
        {
            throw new YtDlpBinaryDownloadException("The ffbinaries API returned a malformed payload.", apiUrl);
        }

        var urls = SelectFfBinariesUrls(response.Bin);
        if (urls is null)
        {
            throw new PlatformNotSupportedException(
                $"ffbinaries does not provide a build for {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}.");
        }

        return urls;
    }

    private static FfBinariesUrls? SelectFfBinariesUrls(FfBinariesPlatforms platforms)
    {
        var arch = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
        {
            return arch == Architecture.X64 ? platforms.Windows64 : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return arch == Architecture.X64 ? platforms.Osx64 : null;
        }

        if (OperatingSystem.IsLinux())
        {
            return arch switch
            {
                Architecture.X64 => platforms.Linux64,
                Architecture.X86 => platforms.Linux32,
                Architecture.Arm64 => platforms.LinuxArm64,
                Architecture.Arm => platforms.LinuxArmHf,
                _ => null
            };
        }

        return null;
    }

    private string ResolveYtDlpUrl()
    {
        var arch = RuntimeInformation.OSArchitecture;
        var baseUrl = _options.YtDlpReleaseBaseUrl;
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        if (OperatingSystem.IsWindows())
        {
            var asset = arch == Architecture.X86 ? "yt-dlp_x86.exe" : "yt-dlp.exe";
            return baseUrl + asset;
        }

        if (OperatingSystem.IsMacOS())
        {
            return baseUrl + "yt-dlp_macos";
        }

        if (OperatingSystem.IsLinux())
        {
            var asset = arch switch
            {
                Architecture.X64 => "yt-dlp_linux",
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => throw new PlatformNotSupportedException(
                    $"yt-dlp does not provide a Linux release asset for {arch}.")
            };
            return baseUrl + asset;
        }

        throw new PlatformNotSupportedException(
            $"yt-dlp does not provide a release asset for {RuntimeInformation.OSDescription}.");
    }

    private static string ResolveDenoTarget()
    {
        var arch = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
        {
            return arch == Architecture.X64
                ? "x86_64-pc-windows-msvc"
                : throw new PlatformNotSupportedException($"Deno does not provide a Windows {arch} build.");
        }

        if (OperatingSystem.IsMacOS())
        {
            return arch switch
            {
                Architecture.X64 => "x86_64-apple-darwin",
                Architecture.Arm64 => "aarch64-apple-darwin",
                _ => throw new PlatformNotSupportedException($"Deno does not provide a macOS {arch} build.")
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return arch switch
            {
                Architecture.X64 => "x86_64-unknown-linux-gnu",
                Architecture.Arm64 => "aarch64-unknown-linux-gnu",
                _ => throw new PlatformNotSupportedException($"Deno does not provide a Linux {arch} build.")
            };
        }

        throw new PlatformNotSupportedException(
            $"Deno does not provide a build for {RuntimeInformation.OSDescription}.");
    }

    private string ExpectedYtDlpFileName() => ExtractFileNameFromUrl(ResolveYtDlpUrl());

    private static string ExpectedFfBinaryFileName(BinaryKind kind) =>
        (kind, OperatingSystem.IsWindows()) switch
        {
            (BinaryKind.Ffmpeg, true) => "ffmpeg.exe",
            (BinaryKind.Ffmpeg, false) => "ffmpeg",
            (BinaryKind.Ffprobe, true) => "ffprobe.exe",
            (BinaryKind.Ffprobe, false) => "ffprobe",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an ffbinaries kind.")
        };

    private static string ExpectedDenoFileName() =>
        OperatingSystem.IsWindows() ? "deno.exe" : "deno";

    private string ResolveDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            return Path.GetFullPath(directory);
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultDirectory))
        {
            return Path.GetFullPath(_options.DefaultDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ExtractFileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.AbsolutePath))
        {
            var name = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        throw new YtDlpBinaryDownloadException(
            $"Could not derive a file name from URL '{url}'.",
            url);
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        BinaryKind kind,
        IProgress<BinaryDownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tempPath = destination + ".download";
        TryDeleteFile(tempPath);

        try
        {
            await using (var fileStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             useAsync: true))
            {
                await DownloadToStreamAsync(url, fileStream, kind, progress, ct).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            TryDeleteFile(tempPath);
            throw new YtDlpBinaryDownloadException($"Failed to download {kind} from {url}.", url, ex);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        try
        {
            File.Move(tempPath, destination, overwrite: true);
        }
        catch (IOException ex)
        {
            TryDeleteFile(tempPath);
            throw new YtDlpBinaryDownloadException(
                $"Failed to move downloaded {kind} into place at '{destination}'.",
                url,
                ex);
        }
    }

    private async Task<string> DownloadAndExtractZipAsync(
        string url,
        string entryPrefix,
        string targetDirectory,
        BinaryKind kind,
        IProgress<BinaryDownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetDirectory);

        await using var memory = new MemoryStream();
        try
        {
            await DownloadToStreamAsync(url, memory, kind, progress, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new YtDlpBinaryDownloadException($"Failed to download {kind} from {url}.", url, ex);
        }

        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = FindBinaryEntry(archive, entryPrefix);
        if (entry is null)
        {
            throw new YtDlpBinaryDownloadException(
                $"Could not find a binary matching '{entryPrefix}' in the archive at {url}.",
                url);
        }

        var destination = Path.Combine(targetDirectory, entry.Name);
        var tempPath = destination + ".download";
        TryDeleteFile(tempPath);

        try
        {
            await using (var entryStream = entry.Open())
            await using (var fileStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             useAsync: true))
            {
                await entryStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        try
        {
            File.Move(tempPath, destination, overwrite: true);
        }
        catch (IOException ex)
        {
            TryDeleteFile(tempPath);
            throw new YtDlpBinaryDownloadException(
                $"Failed to move extracted {kind} into place at '{destination}'.",
                url,
                ex);
        }

        ApplyUnixExecBit(destination);
        return destination;
    }

    private static ZipArchiveEntry? FindBinaryEntry(ZipArchive archive, string entryPrefix)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(entry.Name);
            if (string.Equals(stem, entryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private async Task DownloadToStreamAsync(
        string url,
        Stream destination,
        BinaryKind kind,
        IProgress<BinaryDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long received = 0;
            while (true)
            {
                var read = await source
                    .ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), ct)
                    .ConfigureAwait(false);
                received += read;

                progress?.Report(new BinaryDownloadProgress
                {
                    Kind = kind,
                    Url = url,
                    BytesReceived = received,
                    TotalBytes = totalBytes
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetUnixFileMode(string path)
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Setting the executable bit is best-effort; failure to chmod must not fail the download.")]
    private static void ApplyUnixExecBit(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetUnixFileMode(path);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Extracts every file entry of <paramref name="archive"/> under <paramref name="destinationRoot"/>,
    /// preserving the archive's directory structure and guarding against path-traversal ("zip slip").
    /// </summary>
    private static void ExtractArchive(ZipArchive archive, string destinationRoot, string sourceUrl)
    {
        var rootFull = Path.GetFullPath(destinationRoot + Path.DirectorySeparatorChar);

        foreach (var entry in archive.Entries)
        {
            // Directory entries have an empty Name; their structure is recreated from file paths below.
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!destination.StartsWith(rootFull, StringComparison.Ordinal))
            {
                throw new YtDlpBinaryDownloadException(
                    $"Archive entry '{entry.FullName}' escapes the extraction directory.",
                    sourceUrl);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort cleanup of a staging directory; surface the real error rather than the cleanup failure.")]
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort cleanup of staging files; surface the real error rather than the cleanup failure.")]
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
