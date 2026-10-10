using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using YtDlpSharpLib.Downloads;
using YtDlpSharpLib.Exceptions;
using YtDlpSharpLib.Internal;
using YtDlpSharpLib.Models;
using YtDlpSharpLib.Options;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Progress;
using YtDlpSharpLib.Rendering;

namespace YtDlpSharpLib;

/// <summary>
/// Default <see cref="IYtDlpClient"/> implementation. Composes a <see cref="IYtDlpProcessFactory"/>,
/// an <see cref="IYtDlpArgumentRenderer"/>, and a <see cref="TimeProvider"/> to launch and observe
/// yt-dlp child processes.
/// </summary>
public sealed class YtDlpClient : IYtDlpClient
{
    private readonly YtDlpClientOptions _options;
    private readonly IYtDlpProcessFactory _factory;
    private readonly IYtDlpArgumentRenderer _renderer;
    private readonly TimeProvider _timeProvider;
    private readonly IYtDlpProcessStartGate _processStartGate;
    private YtDlpOptions _defaultYtDlpOptions;

    /// <summary>
    /// Creates a client with default services wired up. Suitable for simple console / direct
    /// usage without dependency injection.
    /// </summary>
    public YtDlpClient(YtDlpClientOptions? options = null)
        : this(
            options ?? new YtDlpClientOptions(),
            new YtDlpProcessFactory(),
            new YtDlpArgumentRenderer(),
            TimeProvider.System)
    {
    }

    /// <summary>Creates a client from typed options. Suitable for direct (non-DI) usage.</summary>
    public YtDlpClient(
        YtDlpClientOptions options,
        IYtDlpProcessFactory processFactory,
        IYtDlpArgumentRenderer argumentRenderer,
        TimeProvider timeProvider,
        IYtDlpProcessStartGate? processStartGate = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(argumentRenderer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        _factory = processFactory;
        _renderer = argumentRenderer;
        _timeProvider = timeProvider;
        _processStartGate = processStartGate ?? new YtDlpProcessStartGate(options, timeProvider);
        _defaultYtDlpOptions = BuildDefaultYtDlpOptions(options);
    }

    /// <summary>Creates a client from <see cref="IOptions{TOptions}"/>. Used by the DI container.</summary>
    public YtDlpClient(
        IOptions<YtDlpClientOptions> options,
        IYtDlpProcessFactory processFactory,
        IYtDlpArgumentRenderer argumentRenderer,
        TimeProvider timeProvider,
        IYtDlpProcessStartGate processStartGate)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Value,
            processFactory,
            argumentRenderer,
            timeProvider,
            processStartGate)
    {
    }

    /// <inheritdoc />
    public string? OutputFolder
    {
        get => FromHomePath(_defaultYtDlpOptions.Filesystem.Paths);
        set => _defaultYtDlpOptions = _defaultYtDlpOptions with
        {
            Filesystem = _defaultYtDlpOptions.Filesystem with
            {
                Paths = ToHomePath(value)
            }
        };
    }

    /// <inheritdoc />
    public string? OutputFileTemplate
    {
        get => _defaultYtDlpOptions.Filesystem.Output;
        set => _defaultYtDlpOptions = _defaultYtDlpOptions with
        {
            Filesystem = _defaultYtDlpOptions.Filesystem with
            {
                Output = string.IsNullOrWhiteSpace(value) ? null : value
            }
        };
    }

    /// <inheritdoc />
    public bool RestrictFilenames
    {
        get => _defaultYtDlpOptions.Filesystem.RestrictFilenames;
        set => _defaultYtDlpOptions = _defaultYtDlpOptions with
        {
            Filesystem = _defaultYtDlpOptions.Filesystem with
            {
                RestrictFilenames = value
            }
        };
    }

    /// <inheritdoc />
    public bool OverwriteFiles
    {
        get => _defaultYtDlpOptions.Filesystem.ForceOverwrites;
        set => _defaultYtDlpOptions = _defaultYtDlpOptions with
        {
            Filesystem = _defaultYtDlpOptions.Filesystem with
            {
                ForceOverwrites = value
            }
        };
    }

    /// <inheritdoc />
    public bool IgnoreDownloadErrors
    {
        get => _defaultYtDlpOptions.General.IgnoreErrors;
        set => _defaultYtDlpOptions = _defaultYtDlpOptions with
        {
            General = _defaultYtDlpOptions.General with
            {
                IgnoreErrors = value
            }
        };
    }

    /// <inheritdoc />
    public Task<RunResult<YtDlpProcessResult>> RunWithOptions(
        string url,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default) =>
        RunWithOptionsAsync(url, options, workingDirectory, progress, ct);

    /// <inheritdoc />
    public Task<RunResult<YtDlpProcessResult>> RunWithOptions(
        IEnumerable<string> urls,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default) =>
        RunWithOptionsAsync(urls, options, workingDirectory, progress, ct);

    /// <inheritdoc />
    public Task<RunResult<YtDlpProcessResult>> RunWithOptionsAsync(
        string url,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return RunWithOptionsAsync([url], options, workingDirectory, progress, ct);
    }

    /// <inheritdoc />
    public async Task<RunResult<YtDlpProcessResult>> RunWithOptionsAsync(
        IEnumerable<string> urls,
        YtDlpOptions options,
        string? workingDirectory = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(options);

        var urlList = urls as IReadOnlyList<string> ?? urls.ToArray();
        foreach (var url in urlList)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
        }

        var rendered = _renderer.Render(options);
        var arguments = new List<string>(rendered.Count + urlList.Count);
        arguments.AddRange(rendered);
        arguments.AddRange(urlList);

        var startInfo = new YtDlpProcessStartInfo
        {
            ExecutablePath = _options.YtDlpExecutablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = _options.EnvironmentVariables,
            RawStdoutWriter = _options.StdoutForwardingWriter,
            RawStderrWriter = _options.StderrForwardingWriter
        };

        try
        {
            var result = await RunCapturingAsync(startInfo, progress, ct).ConfigureAwait(false);
            return RunResult<YtDlpProcessResult>.Succeeded(result);
        }
        catch (YtDlpException ex)
        {
            return RunResult<YtDlpProcessResult>.Failed(GetErrorOutput(ex));
        }
    }

    /// <inheritdoc />
    public async Task<VideoInfo> GetVideoInfoAsync(
        string url,
        CancellationToken ct = default,
        bool flat = false,
        bool fetchComments = false,
        YtDlpOptions? overrideOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var args = new List<string>
        {
            "--dump-single-json",
            "--no-playlist"
        };

        if (flat)
        {
            args.Add("--flat-playlist");
        }

        if (fetchComments)
        {
            args.Add("--write-comments");
        }

        if (overrideOptions is not null)
        {
            args.AddRange(_renderer.Render(overrideOptions));
        }

        args.Add(url);

        var startInfo = BuildBareStartInfo(args);
        var stdout = new StringBuilder();
        await RunInternalAsync(
            startInfo,
            (line, _) =>
            {
                stdout.AppendLine(line);
                return ValueTask.CompletedTask;
            },
            ct).ConfigureAwait(false);

        return DeserializeVideoInfo(stdout.ToString());
    }

    /// <inheritdoc />
    public async Task<RunResult<VideoInfo>> TryGetVideoInfoAsync(
        string url,
        CancellationToken ct = default,
        bool flat = false,
        bool fetchComments = false,
        YtDlpOptions? overrideOptions = null)
    {
        try
        {
            var info = await GetVideoInfoAsync(url, ct, flat, fetchComments, overrideOptions).ConfigureAwait(false);
            return RunResult<VideoInfo>.Succeeded(info);
        }
        catch (YtDlpException ex)
        {
            return RunResult<VideoInfo>.Failed(GetErrorOutput(ex));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<VideoInfo> GetPlaylistInfoAsync(
        string url,
        [EnumeratorCancellation] CancellationToken ct = default,
        YtDlpOptions? overrideOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var args = new List<string>
        {
            "--dump-json",
            "--yes-playlist",
            "--ignore-no-formats-error"
        };

        if (overrideOptions is not null)
        {
            args.AddRange(_renderer.Render(overrideOptions));
        }

        args.Add(url);

        var startInfo = BuildBareStartInfo(args);

        await foreach (var line in StreamStdoutAsync(startInfo, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return DeserializeVideoInfo(line);
        }
    }

    /// <inheritdoc />
    public Task DownloadAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new DownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        return RunDownloadAsync(url, outputDirectory, ytDlp, progress, ct);
    }

    /// <inheritdoc />
    public async Task<RunResult> TryDownloadAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            await DownloadAsync(url, outputDirectory, options, progress, ct).ConfigureAwait(false);
            return RunResult.Succeeded();
        }
        catch (YtDlpException ex)
        {
            return RunResult.Failed(GetErrorOutput(ex));
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<YtDlpProgress> DownloadWithProgressAsync(
        string url,
        string outputDirectory,
        DownloadOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new DownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        return StreamProgressAsync(url, outputDirectory, ytDlp, ct);
    }

    /// <inheritdoc />
    public Task DownloadAudioAsync(
        string url,
        string outputDirectory,
        AudioDownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new AudioDownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        ytDlp = ytDlp with
        {
            PostProcessing = ytDlp.PostProcessing with
            {
                ExtractAudio = true,
                AudioFormat = ToAudioConversionFormat(resolved.AudioFormat)
            }
        };
        return RunDownloadAsync(url, outputDirectory, ytDlp, progress, ct);
    }

    /// <inheritdoc />
    public Task DownloadPlaylistAsync(
        string url,
        string outputDirectory,
        PlaylistDownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new PlaylistDownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        ytDlp = ytDlp with
        {
            VideoSelection = ytDlp.VideoSelection with
            {
                YesPlaylist = true,
                PlaylistItems = resolved.PlaylistItems ?? ytDlp.VideoSelection.PlaylistItems
            }
        };
        return RunDownloadAsync(url, outputDirectory, ytDlp, progress, ct);
    }

    /// <inheritdoc />
    public Task DownloadAudioPlaylistAsync(
        string url,
        string outputDirectory,
        AudioPlaylistDownloadOptions? options = null,
        IProgress<YtDlpProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new AudioPlaylistDownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        ytDlp = ytDlp with
        {
            PostProcessing = ytDlp.PostProcessing with
            {
                ExtractAudio = true,
                AudioFormat = ToAudioConversionFormat(resolved.AudioFormat)
            },
            VideoSelection = ytDlp.VideoSelection with
            {
                YesPlaylist = true,
                PlaylistItems = resolved.PlaylistItems ?? ytDlp.VideoSelection.PlaylistItems
            }
        };
        return RunDownloadAsync(url, outputDirectory, ytDlp, progress, ct);
    }

    /// <inheritdoc />
    public Task DownloadMetadataAsync(
        string url,
        string outputDirectory,
        MetadataDownloadOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new MetadataDownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        ytDlp = ytDlp with
        {
            Filesystem = ytDlp.Filesystem with
            {
                WriteInfoJson = true
            },
            Thumbnail = ytDlp.Thumbnail with
            {
                WriteThumbnail = ytDlp.Thumbnail.WriteThumbnail || resolved.WriteThumbnail
            },
            Subtitle = ytDlp.Subtitle with
            {
                WriteSubs = ytDlp.Subtitle.WriteSubs || resolved.WriteSubtitles,
                SubLangs = ytDlp.Subtitle.SubLangs ?? resolved.SubtitleLanguages
            },
            VerbositySimulation = ytDlp.VerbositySimulation with
            {
                SkipDownload = true
            }
        };

        return RunDownloadAsync(url, outputDirectory, ytDlp, progress: null, ct);
    }

    /// <inheritdoc />
    public Task DownloadLiveChatAsync(
        string url,
        string outputDirectory,
        LiveChatDownloadOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolved = options ?? new LiveChatDownloadOptions();
        var ytDlp = ApplyDownloadDefaults(ComposeDefaultOptions(resolved.YtDlp, resolved), outputDirectory);
        ytDlp = ApplyConvenienceFlags(ytDlp, resolved);
        ytDlp = ytDlp with
        {
            Subtitle = ytDlp.Subtitle with
            {
                WriteSubs = true,
                SubLangs = "live_chat"
            },
            VerbositySimulation = ytDlp.VerbositySimulation with
            {
                SkipDownload = true
            }
        };

        return RunDownloadAsync(url, outputDirectory, ytDlp, progress: null, ct);
    }

    /// <inheritdoc />
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var startInfo = BuildBareStartInfo(["--version"]);
        var stdout = new StringBuilder();

        await RunInternalAsync(
            startInfo,
            (line, _) =>
            {
                stdout.AppendLine(line);
                return ValueTask.CompletedTask;
            },
            ct).ConfigureAwait(false);

        return stdout.ToString().Trim();
    }

    /// <inheritdoc />
    public Task RunUpdateAsync(CancellationToken ct = default) =>
        RunInternalAsync(BuildBareStartInfo(["--update"]), handleStdoutLine: null, ct);

    private Task RunDownloadAsync(
        string url,
        string outputDirectory,
        YtDlpOptions ytDlpOptions,
        IProgress<YtDlpProgress>? progress,
        CancellationToken ct)
    {
        var startInfo = BuildDownloadStartInfo(ytDlpOptions, outputDirectory, url);

        Func<string, CancellationToken, ValueTask>? handler = null;
        if (progress is not null)
        {
            handler = (line, _) =>
            {
                if (ProgressLineParser.TryParse(line, out var parsed))
                {
                    progress.Report(parsed);
                }
                return ValueTask.CompletedTask;
            };
        }

        return RunInternalAsync(startInfo, handler, ct);
    }

    private async IAsyncEnumerable<YtDlpProgress> StreamProgressAsync(
        string url,
        string outputDirectory,
        YtDlpOptions ytDlpOptions,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var startInfo = BuildDownloadStartInfo(ytDlpOptions, outputDirectory, url);

        await foreach (var line in StreamStdoutAsync(startInfo, ct).ConfigureAwait(false))
        {
            if (ProgressLineParser.TryParse(line, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private async IAsyncEnumerable<string> StreamStdoutAsync(
        YtDlpProcessStartInfo startInfo,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await _processStartGate.WaitForTurnAsync(ct).ConfigureAwait(false);
        await using var process = _factory.Create(startInfo);
        var stderrBuffer = new RingBuffer<string>(_options.StderrTailLineCount);
        var stderrTask = StartStderrTailingAsync(process, stderrBuffer);

        await process.StartAsync(ct).ConfigureAwait(false);

        var natural = false;
        try
        {
            await foreach (var line in process.StdoutLines.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return line;
            }

            natural = true;
        }
        finally
        {
            if (!natural)
            {
                await GracefulCancelAsync(process).ConfigureAwait(false);
            }

            await SafeAwaitAsync(stderrTask).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var exitCode = process.ExitCode ?? -1;
        if (exitCode != 0)
        {
            throw new YtDlpProcessException(
                $"yt-dlp exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.",
                command: BuildCommandSummary(startInfo),
                exitCode: exitCode,
                lastStderrLines: JoinStderr(stderrBuffer));
        }
    }

    private async Task RunInternalAsync(
        YtDlpProcessStartInfo startInfo,
        Func<string, CancellationToken, ValueTask>? handleStdoutLine,
        CancellationToken ct)
    {
        await _processStartGate.WaitForTurnAsync(ct).ConfigureAwait(false);
        await using var process = _factory.Create(startInfo);
        var stderrBuffer = new RingBuffer<string>(_options.StderrTailLineCount);

        var stderrTask = StartStderrTailingAsync(process, stderrBuffer);
        var stdoutTask = StartStdoutConsumingAsync(process, handleStdoutLine);

        await process.StartAsync(ct).ConfigureAwait(false);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await GracefulCancelAsync(process).ConfigureAwait(false);
            throw;
        }

        await SafeAwaitAsync(stdoutTask).ConfigureAwait(false);
        await SafeAwaitAsync(stderrTask).ConfigureAwait(false);

        var exitCode = process.ExitCode ?? -1;
        if (exitCode != 0)
        {
            throw new YtDlpProcessException(
                $"yt-dlp exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.",
                command: BuildCommandSummary(startInfo),
                exitCode: exitCode,
                lastStderrLines: JoinStderr(stderrBuffer));
        }
    }

    private async Task<YtDlpProcessResult> RunCapturingAsync(
        YtDlpProcessStartInfo startInfo,
        IProgress<YtDlpProgress>? progress,
        CancellationToken ct)
    {
        await _processStartGate.WaitForTurnAsync(ct).ConfigureAwait(false);
        await using var process = _factory.Create(startInfo);
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var stderrBuffer = new RingBuffer<string>(_options.StderrTailLineCount);

        var stderrTask = Task.Run(async () =>
        {
            await foreach (var line in process.StderrLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                }
                stderrBuffer.Add(line);
            }
        }, CancellationToken.None);

        var stdoutTask = Task.Run(async () =>
        {
            await foreach (var line in process.StdoutLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                lock (stdoutLines)
                {
                    stdoutLines.Add(line);
                }

                if (progress is not null && ProgressLineParser.TryParse(line, out var parsed))
                {
                    progress.Report(parsed);
                }
            }
        }, CancellationToken.None);

        await process.StartAsync(ct).ConfigureAwait(false);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await GracefulCancelAsync(process).ConfigureAwait(false);
            throw;
        }

        await SafeAwaitAsync(stdoutTask).ConfigureAwait(false);
        await SafeAwaitAsync(stderrTask).ConfigureAwait(false);

        var exitCode = process.ExitCode ?? -1;
        if (exitCode != 0)
        {
            throw new YtDlpProcessException(
                $"yt-dlp exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.",
                command: BuildCommandSummary(startInfo),
                exitCode: exitCode,
                lastStderrLines: JoinStderr(stderrBuffer));
        }

        return new YtDlpProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = string.Join('\n', stdoutLines),
            StandardError = string.Join('\n', stderrLines),
            StandardOutputLines = stdoutLines,
            StandardErrorLines = stderrLines
        };
    }

    private async Task GracefulCancelAsync(IYtDlpProcess process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: false);

        var graceTask = Task.Delay(_options.TerminationGracePeriod, _timeProvider, CancellationToken.None);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var winner = await Task.WhenAny(graceTask, exitTask).ConfigureAwait(false);

        if (winner == graceTask && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await SafeAwaitAsync(process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private static Task StartStderrTailingAsync(IYtDlpProcess process, RingBuffer<string> buffer)
    {
        return Task.Run(async () =>
        {
            await foreach (var line in process.StderrLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                buffer.Add(line);
            }
        }, CancellationToken.None);
    }

    private static Task StartStdoutConsumingAsync(
        IYtDlpProcess process,
        Func<string, CancellationToken, ValueTask>? handler)
    {
        if (handler is null)
        {
            return Task.Run(async () =>
            {
                await foreach (var _ in process.StdoutLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                }
            }, CancellationToken.None);
        }

        return Task.Run(async () =>
        {
            await foreach (var line in process.StdoutLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await handler(line, CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Reader tasks are awaited only to drain side effects; failures surface via process exit code or thrown exceptions.")]
    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string JoinStderr(RingBuffer<string> buffer) =>
        string.Join('\n', buffer.Snapshot());

    private static string GetErrorOutput(YtDlpException exception)
    {
        if (exception is YtDlpProcessException processException
            && !string.IsNullOrWhiteSpace(processException.LastStderrLines))
        {
            return processException.LastStderrLines;
        }

        return exception.Message;
    }

    private static VideoInfo DeserializeVideoInfo(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new YtDlpParsingException("yt-dlp returned no JSON output.");
        }

        try
        {
            var info = JsonSerializer.Deserialize(json, YtDlpJsonContext.Default.VideoInfo);
            return info ?? throw new YtDlpParsingException("yt-dlp returned a null JSON object.");
        }
        catch (JsonException ex)
        {
            throw new YtDlpParsingException("Failed to deserialize yt-dlp JSON output.", ex);
        }
    }

    private YtDlpOptions ComposeDefaultOptions(YtDlpOptions options, DownloadOptions downloadOptions)
    {
        var defaults = _defaultYtDlpOptions;

        return options with
        {
            General = options.General with
            {
                IgnoreErrors = options.General.IgnoreErrors
                               || (defaults.General.IgnoreErrors
                                   && !options.General.AbortOnError
                                   && !downloadOptions.AbortOnError)
            },
            Filesystem = options.Filesystem with
            {
                Paths = string.IsNullOrWhiteSpace(options.Filesystem.Paths)
                    ? defaults.Filesystem.Paths
                    : options.Filesystem.Paths,
                Output = options.Filesystem.Output ?? defaults.Filesystem.Output,
                RestrictFilenames = options.Filesystem.RestrictFilenames
                                    || (defaults.Filesystem.RestrictFilenames
                                        && !options.Filesystem.NoRestrictFilenames),
                ForceOverwrites = options.Filesystem.ForceOverwrites
                                  || (defaults.Filesystem.ForceOverwrites
                                      && !options.Filesystem.NoOverwrites
                                      && !options.Filesystem.NoForceOverwrites)
            },
            Download = options.Download with
            {
                LimitRate = string.IsNullOrWhiteSpace(options.Download.LimitRate)
                    ? defaults.Download.LimitRate
                    : options.Download.LimitRate,
                ThrottledRate = string.IsNullOrWhiteSpace(options.Download.ThrottledRate)
                    ? defaults.Download.ThrottledRate
                    : options.Download.ThrottledRate
            }
        };
    }

    private static YtDlpOptions ApplyConvenienceFlags(YtDlpOptions options, DownloadOptions downloadOptions)
    {
        var general = options.General;
        var filesystem = options.Filesystem;

        if (downloadOptions.OutputTemplate is not null)
        {
            filesystem = filesystem with { Output = downloadOptions.OutputTemplate };
        }

        if (downloadOptions.RestrictFilenames is bool restrict)
        {
            filesystem = filesystem with
            {
                RestrictFilenames = restrict,
                NoRestrictFilenames = !restrict
            };
        }

        if (downloadOptions.OverwriteFiles is bool overwrite)
        {
            filesystem = filesystem with
            {
                ForceOverwrites = overwrite,
                NoOverwrites = !overwrite,
                NoForceOverwrites = !overwrite
            };
        }

        if (downloadOptions.IgnoreDownloadErrors is bool ignore)
        {
            general = general with
            {
                IgnoreErrors = ignore,
                AbortOnError = !ignore
            };
        }

        if (downloadOptions.AbortOnError)
        {
            general = general with { IgnoreErrors = false, AbortOnError = true };
        }

        return options with
        {
            General = general,
            Filesystem = filesystem
        };
    }

    private static YtDlpOptions BuildDefaultYtDlpOptions(YtDlpClientOptions options) =>
        new()
        {
            General = new YtDlpGeneralOptions
            {
                IgnoreErrors = options.IgnoreDownloadErrors
            },
            Filesystem = new YtDlpFilesystemOptions
            {
                Paths = ToHomePath(options.OutputFolder),
                Output = string.IsNullOrWhiteSpace(options.OutputFileTemplate)
                    ? null
                    : options.OutputFileTemplate,
                RestrictFilenames = options.RestrictFilenames,
                ForceOverwrites = options.OverwriteFiles
            },
            Download = new YtDlpDownloadOptions
            {
                LimitRate = string.IsNullOrWhiteSpace(options.DownloadLimitRate)
                    ? null
                    : options.DownloadLimitRate,
                ThrottledRate = string.IsNullOrWhiteSpace(options.DownloadThrottledRate)
                    ? null
                    : options.DownloadThrottledRate
            }
        };

    private static YtDlpOptions ApplyDownloadDefaults(YtDlpOptions options, string outputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(options.Filesystem.Paths))
        {
            return options;
        }

        return options with
        {
            Filesystem = options.Filesystem with { Paths = $"home:{outputDirectory}" }
        };
    }

    private static string? ToHomePath(string? outputFolder) =>
        string.IsNullOrWhiteSpace(outputFolder) ? null : $"home:{outputFolder}";

    private static string? FromHomePath(string? paths)
    {
        const string HomePrefix = "home:";

        if (string.IsNullOrWhiteSpace(paths))
        {
            return null;
        }

        return paths.StartsWith(HomePrefix, StringComparison.Ordinal)
            ? paths[HomePrefix.Length..]
            : paths;
    }

    private static AudioConversionFormat ToAudioConversionFormat(AudioConversionFormat audioFormat) =>
        audioFormat switch
        {
            AudioConversionFormat.M4a => AudioConversionFormat.M4a,
            AudioConversionFormat.Mp3 => AudioConversionFormat.Mp3,
            AudioConversionFormat.Opus => AudioConversionFormat.Opus,
            AudioConversionFormat.Flac => AudioConversionFormat.Flac,
            AudioConversionFormat.Best => AudioConversionFormat.Best,
            AudioConversionFormat.Aac => AudioConversionFormat.Aac,
            AudioConversionFormat.Alac => AudioConversionFormat.Alac,
            AudioConversionFormat.Vorbis => AudioConversionFormat.Vorbis,
            AudioConversionFormat.Wav => AudioConversionFormat.Wav,
            _ => throw new ArgumentOutOfRangeException(nameof(audioFormat), audioFormat, null)
        };

    private YtDlpProcessStartInfo BuildDownloadStartInfo(
        YtDlpOptions options,
        string outputDirectory,
        string url)
    {
        var rendered = _renderer.Render(options);
        var arguments = new List<string>(rendered.Count + 1);
        arguments.AddRange(rendered);
        arguments.Add(url);

        return new YtDlpProcessStartInfo
        {
            ExecutablePath = _options.YtDlpExecutablePath,
            Arguments = arguments,
            WorkingDirectory = outputDirectory,
            EnvironmentVariables = _options.EnvironmentVariables,
            RawStdoutWriter = _options.StdoutForwardingWriter,
            RawStderrWriter = _options.StderrForwardingWriter
        };
    }

    private YtDlpProcessStartInfo BuildBareStartInfo(IReadOnlyList<string> arguments) => new()
    {
        ExecutablePath = _options.YtDlpExecutablePath,
        Arguments = arguments,
        EnvironmentVariables = _options.EnvironmentVariables,
        RawStdoutWriter = _options.StdoutForwardingWriter,
        RawStderrWriter = _options.StderrForwardingWriter
    };

    private static string BuildCommandSummary(YtDlpProcessStartInfo startInfo)
    {
        var sb = new StringBuilder();
        sb.Append(startInfo.ExecutablePath);
        foreach (var arg in startInfo.Arguments)
        {
            sb.Append(' ').Append(arg);
        }

        return sb.ToString();
    }
}
