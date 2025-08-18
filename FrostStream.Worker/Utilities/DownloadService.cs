using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace FrostStream.Worker.Utilities;

public class DownloadService
{

    #region Public Functions
    public async Task<string> DownloadVideo(string url, string jobId, OptionSet? optionSet)
    {
        optionSet ??= AgentOptionSets.DefaultOptionSet;
        var ytdl = CreateYtDlpInstance(jobId);
        IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(p =>
        {
            var fixedProgress = Math.Clamp(p.Progress * 100, 0, 100);
            var str = $"State:{p.State}, Prog:{fixedProgress}, Speed:{p.DownloadSpeed}, TotalDlSize:{p.TotalDownloadSize}, ETA:{p.ETA}";
            Console.WriteLine(str);

        });


        var output = await ytdl.RunVideoDownload(url, progress: progress, overrideOptions: optionSet);
        if (output != null && output.Success == true)
        {
            return output.Data;
        }
        return "";
    }

    #endregion


    #region Internal Fields/Functions
    private static readonly string _ytdlpPath = Path.Combine(Program.TOOLS_PATH, Utils.YtDlpBinaryName);
    private static readonly string _ffmpegPath = Path.Combine(Program.TOOLS_PATH, Utils.FfmpegBinaryName);
    private static readonly string _ffprobePath = Path.Combine(Program.TOOLS_PATH, Utils.FfprobeBinaryName);

    public YoutubeDL CreateYtDlpInstance(string jobIdName)
    {
        var ytdl = new YoutubeDL()
        {
            YoutubeDLPath =_ytdlpPath,
            FFmpegPath = _ffmpegPath,
            OutputFileTemplate = $"{jobIdName}/%(extractor)s_[%(id)s]_%(upload_date)s.%(ext)s",
            OutputFolder = Path.Combine(Program.DOWNLOAD_PATH, jobIdName)
        };
        return ytdl;
    }

    #endregion
}