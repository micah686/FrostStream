using System.Runtime.InteropServices;

namespace Worker.Services;

/// <summary>
/// Resolves the predicted on-disk filenames the YtDlpSharpLib binary downloader will produce
/// for the current OS/architecture. Matches <c>YtDlpBinaryDownloader.ResolveYtDlpUrl</c> and
/// <c>YtDlpBinaryDownloader.ExpectedFfBinaryFileName</c>.
/// </summary>
internal static class YtDlpPaths
{
    public static string YtDlpFileName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return RuntimeInformation.OSArchitecture == Architecture.X86
                    ? "yt-dlp_x86.exe"
                    : "yt-dlp.exe";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "yt-dlp_macos";
            }

            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux"
            };
        }
    }

    public static string FfmpegFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

    public static string FfprobeFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
}
