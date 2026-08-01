using System.Diagnostics;
using System.Threading.Channels;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Progress;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib.Tests;

public sealed class YtDlpClientTests
{
    private const string VimeoUrl = "https://vimeo.com/1084537";

    [Fact]
    public async Task GetVideoInfoAsync_UsesSingleJsonModeAndParsesVimeoMetadata()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            [
                """
                {"id":"1084537","title":"Big Buck Bunny","extractor":"vimeo","upload_date":"20080530","duration":596,"webpage_url":"https://vimeo.com/1084537","formats":[{"format_id":"http-540p","format_note":"540p","ext":"mp4","resolution":"960x540","filesize":1234567,"tbr":900.5,"vcodec":"avc1","acodec":"mp4a"}],"thumbnails":[{"id":"0","url":"https://i.vimeocdn.com/video/1.jpg","width":960,"height":540,"preference":1}],"chapters":[{"title":"Opening","start_time":0.0,"end_time":12.5}],"subtitles":{"en":[{"ext":"vtt","url":"https://example.test/subs.vtt","name":"English"}]}}
                """
            ]));
        var client = CreateClient(factory);

        var info = await client.GetVideoInfoAsync(VimeoUrl);

        Assert.Equal(["--dump-single-json", "--no-playlist", VimeoUrl], factory.SingleStartInfo.Arguments);
        Assert.Equal("1084537", info.Id);
        Assert.Equal("Big Buck Bunny", info.Title);
        Assert.Equal("vimeo", info.Extractor);
        Assert.Equal(new DateOnly(2008, 5, 30), info.ParsedUploadDate);
        Assert.Equal(596, info.Duration);
        Assert.Equal(VimeoUrl, info.WebpageUrl);
        var format = Assert.Single(info.Formats!);
        Assert.Equal("http-540p", format.FormatId);
        Assert.Equal("960x540", format.Resolution);
        Assert.Equal(1234567, format.Filesize);
        Assert.Equal("avc1", format.Vcodec);
        Assert.NotNull(info.Thumbnails);
        Assert.Single(info.Thumbnails);
        Assert.NotNull(info.Chapters);
        Assert.Single(info.Chapters);
        Assert.NotNull(info.Subtitles);
        var subtitleTracks = Assert.Single(info.Subtitles).Value;
        Assert.Single(subtitleTracks);
    }

    [Fact]
    public async Task GetPlaylistInfoAsync_UsesPlaylistJsonModeAndStreamsEntries()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            [
                MinimalVideoJson("one", "First clip"),
                MinimalVideoJson("two", "Second clip")
            ]));
        var client = CreateClient(factory);

        var entries = new List<VideoInfo>();
        await foreach (var entry in client.GetPlaylistInfoAsync(VimeoUrl))
        {
            entries.Add(entry);
        }

        Assert.Equal(["--dump-json", "--yes-playlist", "--ignore-no-formats-error", VimeoUrl], factory.SingleStartInfo.Arguments);
        Assert.Collection(
            entries,
            first => Assert.Equal("First clip", first.Title),
            second => Assert.Equal("Second clip", second.Title));
    }

    [Fact]
    public async Task DownloadAudioAsync_AddsOutputPathAndAudioExtractionOptions()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(factory);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await client.DownloadAudioAsync(
                VimeoUrl,
                outputDirectory,
                new AudioDownloadOptions { AudioFormat = AudioConversionFormat.Mp3 });

            Assert.Equal(outputDirectory, factory.SingleStartInfo.WorkingDirectory);
            Assert.Equal(
                ["--paths", $"home:{outputDirectory}", "--extract-audio", "--audio-format", "mp3", VimeoUrl],
                factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_AppliesClientOptionConvenienceDefaults()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(
            factory,
            new YtDlpClientOptions
            {
                YtDlpExecutablePath = "yt-dlp-test",
                OutputFolder = "/configured",
                OutputFileTemplate = "%(title)s.%(ext)s",
                RestrictFilenames = true,
                OverwriteFiles = true,
                IgnoreDownloadErrors = true
            });
        var outputDirectory = CreateTempDirectory();

        try
        {
            await client.DownloadAsync(VimeoUrl, outputDirectory);

            Assert.Equal(
                [
                    "--ignore-errors",
                    "--paths",
                    "home:/configured",
                    "--output",
                    "%(title)s.%(ext)s",
                    "--restrict-filenames",
                    "--force-overwrites",
                    VimeoUrl
                ],
                factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_AppliesClientDownloadThrottleDefaults()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(
            factory,
            new YtDlpClientOptions
            {
                YtDlpExecutablePath = "yt-dlp-test",
                DownloadLimitRate = "500K",
                DownloadThrottledRate = "100K"
            });
        var outputDirectory = CreateTempDirectory();

        try
        {
            await client.DownloadAsync(VimeoUrl, outputDirectory);

            Assert.Equal(
                [
                    "--limit-rate",
                    "500K",
                    "--throttled-rate",
                    "100K",
                    "--paths",
                    $"home:{outputDirectory}",
                    VimeoUrl
                ],
                factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_ClientConveniencePropertiesCanBeMutatedAtRuntime()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(factory);
        var outputDirectory = CreateTempDirectory();

        client.OutputFolder = "/runtime";
        client.OutputFileTemplate = "%(id)s.%(ext)s";
        client.RestrictFilenames = true;
        client.OverwriteFiles = true;
        client.IgnoreDownloadErrors = true;

        try
        {
            await client.DownloadAsync(VimeoUrl, outputDirectory);

            Assert.Equal("/runtime", client.OutputFolder);
            Assert.Equal("%(id)s.%(ext)s", client.OutputFileTemplate);
            Assert.True(client.RestrictFilenames);
            Assert.True(client.OverwriteFiles);
            Assert.True(client.IgnoreDownloadErrors);
            Assert.Equal(
                [
                    "--ignore-errors",
                    "--paths",
                    "home:/runtime",
                    "--output",
                    "%(id)s.%(ext)s",
                    "--restrict-filenames",
                    "--force-overwrites",
                    VimeoUrl
                ],
                factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_PerCallOptionsOverrideClientConvenienceDefaults()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(
            factory,
            new YtDlpClientOptions
            {
                YtDlpExecutablePath = "yt-dlp-test",
                OutputFolder = "/configured",
                OutputFileTemplate = "%(title)s.%(ext)s",
                RestrictFilenames = true,
                OverwriteFiles = true,
                IgnoreDownloadErrors = true,
                DownloadLimitRate = "500K",
                DownloadThrottledRate = "100K"
            });
        var outputDirectory = CreateTempDirectory();

        try
        {
            await client.DownloadAsync(
                VimeoUrl,
                outputDirectory,
                new DownloadOptions
                {
                    YtDlp = new YtDlpOptions
                    {
                        General = new YtDlpGeneralOptions
                        {
                            AbortOnError = true
                        },
                        Filesystem = new YtDlpFilesystemOptions
                        {
                            Paths = "home:/per-call",
                            Output = "%(id)s.%(ext)s",
                            NoRestrictFilenames = true,
                            NoOverwrites = true
                        },
                        Download = new YtDlpDownloadOptions
                        {
                            LimitRate = "1M",
                            ThrottledRate = "250K"
                        }
                    }
                });

            Assert.Equal(
                [
                    "--abort-on-error",
                    "--limit-rate",
                    "1M",
                    "--throttled-rate",
                    "250K",
                    "--paths",
                    "home:/per-call",
                    "--output",
                    "%(id)s.%(ext)s",
                    "--no-restrict-filenames",
                    "--no-overwrites",
                    VimeoUrl
                ],
                factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadMetadataAsync_CombinesMetadataFlagsWithoutOverwritingExplicitPath()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(factory);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await client.DownloadMetadataAsync(
                VimeoUrl,
                outputDirectory,
                new MetadataDownloadOptions
                {
                    WriteThumbnail = true,
                    WriteSubtitles = true,
                    SubtitleLanguages = "en",
                    YtDlp = new YtDlpOptions
                    {
                        Filesystem = new YtDlpFilesystemOptions { Paths = "home:/already-set" }
                    }
                });

            Assert.Equal(
                [
                    "--paths",
                    "home:/already-set",
                    "--write-info-json",
                    "--write-thumbnail",
                    "--skip-download",
                    "--write-subs",
                    "--sub-langs",
                    "en",
                    VimeoUrl
                ],
                factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadWithProgressAsync_ParsesDownloadAndPostProcessLines()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            [
                "[download]  50.0% of 10.00MiB at 2.00MiB/s ETA 00:05",
                "[Merger] Merging formats into \"video.mp4\"",
                "plain status line"
            ]));
        var client = CreateClient(factory);
        var outputDirectory = CreateTempDirectory();

        try
        {
            var progress = new List<YtDlpProgress>();
            await foreach (var item in client.DownloadWithProgressAsync(VimeoUrl, outputDirectory))
            {
                progress.Add(item);
            }

            Assert.Collection(
                progress,
                downloading =>
                {
                    Assert.Equal(ProgressPhase.Downloading, downloading.Phase);
                    Assert.Equal(50.0, downloading.Percent);
                    Assert.Equal(10 * 1024 * 1024, downloading.TotalBytes);
                    Assert.Equal(5 * 1024 * 1024, downloading.DownloadedBytes);
                    Assert.Equal("2.00MiB/s", downloading.Speed);
                    Assert.Equal(TimeSpan.FromSeconds(5), downloading.Eta);
                },
                merging =>
                {
                    Assert.Equal(ProgressPhase.Merging, merging.Phase);
                    Assert.Equal("Merging formats into \"video.mp4\"", merging.AdditionalInfo);
                });
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TryGetVideoInfoAsync_ReturnsDataOnSuccess()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            [
                MinimalVideoJson("one", "First clip")
            ]));
        var client = CreateClient(factory);

        var result = await client.TryGetVideoInfoAsync(VimeoUrl);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.ErrorOutput);
        Assert.NotNull(result.Data);
        Assert.Equal("First clip", result.Data.Title);
    }

    [Fact]
    public async Task TryGetVideoInfoAsync_ReturnsErrorOutputOnProcessFailure()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            stderrLines:
            [
                "private first",
                "retained error"
            ],
            exitCode: 1));
        var client = CreateClient(
            factory,
            new YtDlpClientOptions
            {
                YtDlpExecutablePath = "yt-dlp-test",
                StderrTailLineCount = 1
            });

        var result = await client.TryGetVideoInfoAsync(VimeoUrl);

        Assert.False(result.Success);
        Assert.Equal("retained error", result.ErrorOutput);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task TryDownloadAsync_ReturnsSuccessWithoutThrowing()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(factory);
        var outputDirectory = CreateTempDirectory();

        try
        {
            var result = await client.TryDownloadAsync(VimeoUrl, outputDirectory);

            Assert.True(result.Success);
            Assert.Equal(string.Empty, result.ErrorOutput);
            Assert.Equal(["--paths", $"home:{outputDirectory}", VimeoUrl], factory.SingleStartInfo.Arguments);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TryDownloadAsync_ReturnsErrorOutputOnProcessFailure()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            stderrLines: ["download failed"],
            exitCode: 2));
        var client = CreateClient(factory);
        var outputDirectory = CreateTempDirectory();

        try
        {
            var result = await client.TryDownloadAsync(VimeoUrl, outputDirectory);

            Assert.False(result.Success);
            Assert.Equal("download failed", result.ErrorOutput);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessFailure_IncludesCommandExitCodeAndConfiguredStderrTail()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess(
            stderrLines:
            [
                "first ignored",
                "second retained",
                "third retained"
            ],
            exitCode: 2));
        var client = CreateClient(
            factory,
            new YtDlpClientOptions
            {
                YtDlpExecutablePath = "yt-dlp-test",
                StderrTailLineCount = 2
            });

        var ex = await Assert.ThrowsAsync<YtDlpProcessException>(() => client.GetVersionAsync());

        Assert.Equal(2, ex.ExitCode);
        Assert.Equal("yt-dlp-test --version", ex.YtDlpCommand);
        Assert.Equal("second retained\nthird retained", ex.LastStderrLines);
    }

    [Fact]
    public async Task ProcessStartGate_IsUsedBeforeEveryProcessStart()
    {
        var factory = new FakeProcessFactory(
            new FakeYtDlpProcess(["1.0.0"]),
            new FakeYtDlpProcess(),
            new FakeYtDlpProcess([MinimalVideoJson("one", "First clip")]));
        var gate = new RecordingProcessStartGate();
        var client = CreateClient(factory, processStartGate: gate);
        var outputDirectory = CreateTempDirectory();

        try
        {
            await client.GetVersionAsync();
            await client.DownloadAsync(VimeoUrl, outputDirectory);
            await foreach (var _ in client.GetPlaylistInfoAsync(VimeoUrl))
            {
            }

            Assert.Equal(3, gate.Calls);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessStartGate_SpacesStarts()
    {
        using var gate = new YtDlpProcessStartGate(
            new YtDlpClientOptions { MinimumDelayBetweenProcessStarts = TimeSpan.FromMilliseconds(30) },
            TimeProvider.System);

        var stopwatch = Stopwatch.StartNew();

        await gate.WaitForTurnAsync(CancellationToken.None);
        await gate.WaitForTurnAsync(CancellationToken.None);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task RunUpdateAsync_InvokesYtDlpUpdatePassthrough()
    {
        var factory = new FakeProcessFactory(new FakeYtDlpProcess());
        var client = CreateClient(factory);

        await client.RunUpdateAsync();

        Assert.Equal(["--update"], factory.SingleStartInfo.Arguments);
    }

    private static YtDlpClient CreateClient(
        FakeProcessFactory factory,
        YtDlpClientOptions? options = null,
        IYtDlpProcessStartGate? processStartGate = null) =>
        new(
            options ?? new YtDlpClientOptions { YtDlpExecutablePath = "yt-dlp-test" },
            factory,
            new YtDlpArgumentRenderer(),
            TimeProvider.System,
            processStartGate);

    private static string MinimalVideoJson(string id, string title) =>
        $$"""
        {"id":"{{id}}","title":"{{title}}","extractor":"vimeo","webpage_url":"{{VimeoUrl}}","formats":[]}
        """;

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "YtDlpSharpLib.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProcessFactory : IYtDlpProcessFactory
    {
        private readonly Queue<FakeYtDlpProcess> _processes;
        private readonly List<YtDlpProcessStartInfo> _startInfos = [];

        public FakeProcessFactory(params FakeYtDlpProcess[] processes)
        {
            _processes = new Queue<FakeYtDlpProcess>(processes);
        }

        public YtDlpProcessStartInfo SingleStartInfo => Assert.Single(_startInfos);

        public IYtDlpProcess Create(YtDlpProcessStartInfo startInfo)
        {
            _startInfos.Add(startInfo);
            Assert.True(_processes.TryDequeue(out var process), "No fake yt-dlp process was configured.");
            return process;
        }
    }

    private sealed class RecordingProcessStartGate : IYtDlpProcessStartGate
    {
        public int Calls { get; private set; }

        public ValueTask WaitForTurnAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeYtDlpProcess : IYtDlpProcess
    {
        private readonly IReadOnlyList<string> _stdoutLines;
        private readonly IReadOnlyList<string> _stderrLines;
        private readonly int _configuredExitCode;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();

        public FakeYtDlpProcess(
            IReadOnlyList<string>? stdoutLines = null,
            IReadOnlyList<string>? stderrLines = null,
            int exitCode = 0)
        {
            _stdoutLines = stdoutLines ?? [];
            _stderrLines = stderrLines ?? [];
            _configuredExitCode = exitCode;
        }

        public ChannelReader<string> StdoutLines => _stdout.Reader;

        public ChannelReader<string> StderrLines => _stderr.Reader;

        public int? ExitCode { get; private set; }

        public bool HasExited => _exited.Task.IsCompleted;

        public int? ProcessId => 1234;

        public async Task StartAsync(CancellationToken ct)
        {
            foreach (var line in _stdoutLines)
            {
                await _stdout.Writer.WriteAsync(line, ct);
            }

            _stdout.Writer.TryComplete();

            foreach (var line in _stderrLines)
            {
                await _stderr.Writer.WriteAsync(line, ct);
            }

            _stderr.Writer.TryComplete();
            ExitCode = _configuredExitCode;
            _exited.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);

        public void Kill(bool entireProcessTree)
        {
            _ = entireProcessTree;
            _stdout.Writer.TryComplete();
            _stderr.Writer.TryComplete();
            ExitCode ??= -1;
            _exited.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            _stdout.Writer.TryComplete();
            _stderr.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
