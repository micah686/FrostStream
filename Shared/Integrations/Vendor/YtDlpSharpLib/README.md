# YtDlpSharpLib

A managed .NET 10 wrapper around the [yt-dlp](https://github.com/yt-dlp/yt-dlp) command-line utility.
The library exposes a strongly-typed `IYtDlpClient`, a reflection-driven argument renderer that
covers every flag in `yt-dlp --help`, an optional binary downloader for yt-dlp/ffmpeg/ffprobe/Deno,
and a small concurrency-limited execution scheduler for batch jobs.

Used https://github.com/Bluegrams/YoutubeDLSharp as a reference for this library.

- Targets `net10.0`.
- Async-first; no blocking calls in the public surface.
- Strongly-typed options grouped by yt-dlp help section (`General`, `Network`, `VideoFormat`,
  `PostProcessing`, `Subtitle`, `Authentication`, `SponsorBlock`, …).
- Direct `RunWithOptionsAsync(...)` entry point for arbitrary single- or multi-URL invocations,
  returning the captured `YtDlpProcessResult`.
- Strongly-typed metadata (channel, uploader, live status, availability, music, series/episode,
  comments, automatic captions, storyboards) with extension-data fallback for forward compatibility.
- Progress reporting via `IProgress<YtDlpProgress>` or `IAsyncEnumerable<YtDlpProgress>`, including
  `TimeSpan? Eta`, post-processor `Destination`/`Message`, and a fallback `Parse(...)` for any
  yt-dlp line.
- Concurrency-bounded scheduler that batches video / audio / playlist / audio-playlist / metadata /
  live-chat jobs in one request model and accepts a per-call concurrency override.
- Typed exceptions (`YtDlpProcessException`, `YtDlpNotFoundException`, `YtDlpValidationException`, …).
- An escape hatch (`RawYtDlpArgument`) for any flag the typed surface does not yet model.

---

## Table of Contents

- [Installation](#installation)
- [Prerequisites](#prerequisites)
- [Quick start (console / non-DI)](#quick-start-console--non-di)
- [Quick start (dependency injection)](#quick-start-dependency-injection)
- [Client API](#client-api)
- [Options model](#options-model)
- [Examples](#examples)
  - [Run yt-dlp directly with typed options](#run-yt-dlp-directly-with-typed-options)
  - [Choosing a format and merge container](#choosing-a-format-and-merge-container)
  - [Audio-only download](#audio-only-download)
  - [Per-call convenience flags](#per-call-convenience-flags)
  - [Output template and filename rules](#output-template-and-filename-rules)
  - [Playlists](#playlists)
  - [Date and filesize filters](#date-and-filesize-filters)
  - [Authentication, cookies, proxies](#authentication-cookies-proxies)
  - [Subtitles](#subtitles)
  - [Thumbnails and metadata sidecars](#thumbnails-and-metadata-sidecars)
  - [Post-processing: recode, remux, embed](#post-processing-recode-remux-embed)
  - [SponsorBlock](#sponsorblock)
  - [Section downloads and rate limits](#section-downloads-and-rate-limits)
  - [Forwarding raw stdout/stderr](#forwarding-raw-stdoutstderr)
  - [Cancellation](#cancellation)
  - [Advanced (raw) arguments](#advanced-raw-arguments)
  - [Composing options with `WithOverrides`](#composing-options-with-withoverrides)
- [Metadata models](#metadata-models)
- [Progress reporting](#progress-reporting)
- [Batching with the execution scheduler](#batching-with-the-execution-scheduler)
- [Binary provisioning (yt-dlp, ffmpeg, ffprobe, Deno)](#binary-provisioning-yt-dlp-ffmpeg-ffprobe-deno)
- [Exceptions](#exceptions)
- [Configuration reference (`YtDlpClientOptions`)](#configuration-reference-ytdlpclientoptions)

---

## Installation

The library is packaged as `YtDlpSharpLib`. From a project that targets `net10.0`:

```bash
dotnet add package YtDlpSharpLib
```

Or as a project reference while developing locally:

```xml
<ItemGroup>
  <ProjectReference Include="..\YtDlpSharpLib\YtDlpSharpLib.csproj" />
</ItemGroup>
```

## Prerequisites

The library shells out to the real `yt-dlp` binary. You must either:

1. Have `yt-dlp` (and, if you are merging or converting media, `ffmpeg`/`ffprobe`) on `PATH`, or
2. Set `YtDlpClientOptions.YtDlpExecutablePath` to an absolute path, or
3. Use the bundled `IYtDlpBinaryDownloader` to fetch the right binaries at runtime — see
   [Binary provisioning](#binary-provisioning-yt-dlp-ffmpeg-ffprobe-deno).

---

## Quick start (console / non-DI)

The simplest constructor takes nothing at all and wires up the default renderer, process factory,
and `TimeProvider`:

```csharp
using YtDlpSharpLib;

var client = new YtDlpClient();   // yt-dlp / ffmpeg resolved from PATH

var info = await client.GetVideoInfoAsync("https://www.youtube.com/watch?v=C0DPdy98e4c");
Console.WriteLine($"{info.Title} ({info.Duration}s)");

await client.DownloadAsync(
    "https://www.youtube.com/watch?v=C0DPdy98e4c",
    outputDirectory: Path.Combine(Environment.CurrentDirectory, "downloads"));
```

Pass a `YtDlpClientOptions` to the same constructor when you need to point at specific binaries
or tweak the defaults:

```csharp
var client = new YtDlpClient(new YtDlpClientOptions
{
    YtDlpExecutablePath  = "/usr/local/bin/yt-dlp",
    FfmpegExecutablePath = "/usr/local/bin/ffmpeg",
});
```

If you want full control over the seams (e.g. supplying a fake `IYtDlpProcessFactory` in tests),
the four-arg constructor is still available:

```csharp
using YtDlpSharpLib;
using YtDlpSharpLib.Process;
using YtDlpSharpLib.Rendering;

var client = new YtDlpClient(
    new YtDlpClientOptions { YtDlpExecutablePath = "yt-dlp", FfmpegExecutablePath = "ffmpeg" },
    new YtDlpProcessFactory(),
    new YtDlpArgumentRenderer(),
    TimeProvider.System);
```

## Quick start (dependency injection)

Register the client (and, optionally, the binary downloader) with `IServiceCollection`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using YtDlpSharpLib;

var services = new ServiceCollection();

services.AddYtDlpClient(opts =>
{
    opts.YtDlpExecutablePath  = "/usr/local/bin/yt-dlp";
    opts.FfmpegExecutablePath = "/usr/local/bin/ffmpeg";
    opts.DownloadConcurrency  = 4;        // used by the scheduler
});

// Optional: register the binary downloader. Pair with HttpClientFactory if you have one.
services.AddYtDlpBinaryDownloader(opts =>
{
    opts.DefaultDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
});

var provider = services.BuildServiceProvider();

var client = provider.GetRequiredService<IYtDlpClient>();
await client.DownloadAsync(
    "https://www.youtube.com/watch?v=C0DPdy98e4c",
    outputDirectory: "downloads");
```

`AddYtDlpClient` registers (all as singletons, all `TryAdd`-style so you can override any of them):

| Service                       | Default implementation        |
|-------------------------------|-------------------------------|
| `IYtDlpClient`                | `YtDlpClient`                 |
| `IYtDlpProcessFactory`        | `YtDlpProcessFactory`         |
| `IYtDlpArgumentRenderer`      | `YtDlpArgumentRenderer`       |
| `IYtDlpExecutionScheduler`    | `YtDlpExecutionScheduler`     |
| `TimeProvider`                | `TimeProvider.System`         |

`AddYtDlpBinaryDownloader` registers `IYtDlpBinaryDownloader` and will use the container's
`HttpClient` if one is registered (e.g. via `services.AddHttpClient<YtDlpBinaryDownloader>()`).
Otherwise the downloader creates and disposes its own.

---

## Client API

`IYtDlpClient` is the entire public download/inspect surface:

| Method                                                                                                    | Maps to                                                  |
|-----------------------------------------------------------------------------------------------------------|----------------------------------------------------------|
| `RunWithOptionsAsync(url, options, workingDir, progress, ct)`                                             | a direct `yt-dlp <rendered options> <url>` invocation    |
| `RunWithOptionsAsync(urls, options, workingDir, progress, ct)`                                            | the same, but with a batch of URLs appended              |
| `GetVideoInfoAsync(url, ct, flat, fetchComments, overrideOptions)`                                        | `--dump-single-json --no-playlist [--flat-playlist] [--write-comments] [<overrides>]` |
| `TryGetVideoInfoAsync(url, ct, flat, fetchComments, overrideOptions)`                                     | the same, returning `RunResult<VideoInfo>` instead of throwing |
| `GetPlaylistInfoAsync(url, ct)`                                                                           | `--dump-json --yes-playlist --ignore-no-formats-error` (streamed) |
| `DownloadAsync(url, outputDirectory, options, progress, ct)`                                              | a regular yt-dlp invocation                              |
| `DownloadWithProgressAsync(url, outputDirectory, options, ct)`                                            | the same, exposed as `IAsyncEnumerable<YtDlpProgress>`   |
| `DownloadAudioAsync(url, outputDirectory, options, progress, ct)`                                         | `-x --audio-format <fmt>`                                |
| `DownloadPlaylistAsync(url, outputDirectory, options, progress, ct)`                                      | `--yes-playlist [--playlist-items …]`                    |
| `DownloadAudioPlaylistAsync(url, outputDirectory, options, progress, ct)`                                 | the union of the previous two                            |
| `DownloadMetadataAsync(url, outputDirectory, options, ct)`                                                | `--write-info-json --skip-download` (+ thumb/subs)       |
| `DownloadLiveChatAsync(url, outputDirectory, options, ct)`                                                | `--write-subs --sub-langs live_chat --skip-download`     |
| `GetVersionAsync(ct)`                                                                                     | `--version`                                              |
| `RunUpdateAsync(ct)`                                                                                      | `--update`                                               |

`outputDirectory` is mandatory for every download method. If you do not set
`YtDlpFilesystemOptions.Paths` yourself, the library will set it to `home:<outputDirectory>` so the
files land where you said they should.

`RunWithOptionsAsync` is the lowest-level entry point: it renders the supplied `YtDlpOptions`
verbatim, appends the URL(s), and returns a `RunResult<YtDlpProcessResult>`. Process failures
become `RunResult.Failed(...)` rather than thrown exceptions.

The `RunWithOptions(...)` overloads (no `Async` suffix) are aliases that call straight into
`RunWithOptionsAsync(...)` for callers that prefer the shorter name.

## Options model

Everything you can pass on the yt-dlp command line is grouped on `YtDlpOptions`:

```csharp
var ytDlp = new YtDlpOptions
{
    General           = new YtDlpGeneralOptions           { /* ... */ },
    Network           = new YtDlpNetworkOptions           { /* ... */ },
    GeoRestriction    = new YtDlpGeoRestrictionOptions    { /* ... */ },
    VideoSelection    = new YtDlpVideoSelectionOptions    { /* ... */ },
    Download          = new YtDlpDownloadOptions          { /* ... */ },
    Filesystem        = new YtDlpFilesystemOptions        { /* ... */ },
    Thumbnail         = new YtDlpThumbnailOptions         { /* ... */ },
    InternetShortcut  = new YtDlpInternetShortcutOptions  { /* ... */ },
    VerbositySimulation = new YtDlpVerbositySimulationOptions { /* ... */ },
    Workarounds       = new YtDlpWorkaroundsOptions       { /* ... */ },
    VideoFormat       = new YtDlpVideoFormatOptions       { /* ... */ },
    Subtitle          = new YtDlpSubtitleOptions          { /* ... */ },
    Authentication    = new YtDlpAuthenticationOptions    { /* ... */ },
    PostProcessing    = new YtDlpPostProcessingOptions    { /* ... */ },
    SponsorBlock      = new YtDlpSponsorBlockOptions      { /* ... */ },
    Extractor         = new YtDlpExtractorOptions         { /* ... */ },
    AdvancedArguments = [ /* RawYtDlpArgument(...) */ ],
};
```

Every group is a `record` with `init`-only properties, so it composes well with `with`-expressions
and is safe to share across calls. To merge a base set of options with a few per-call overrides,
see [Composing options with `WithOverrides`](#composing-options-with-withoverrides).

---

## Examples

### Run yt-dlp directly with typed options

When the high-level `DownloadXxxAsync` shapes do not fit (e.g. you are running a one-off pipeline
that just wants whatever `yt-dlp --print after_move:filepath` produced), use
`RunWithOptionsAsync` and inspect the captured `YtDlpProcessResult`:

```csharp
var result = await client.RunWithOptionsAsync(
    "https://www.youtube.com/watch?v=C0DPdy98e4c",
    new YtDlpOptions
    {
        Filesystem = new YtDlpFilesystemOptions { Paths = "home:downloads" },
        VerbositySimulation = new YtDlpVerbositySimulationOptions
        {
            Print = ["after_move:%(filepath)s"],
        },
    },
    workingDirectory: "downloads");

if (result.Success && result.Data is { } proc)
{
    Console.WriteLine($"yt-dlp exit {proc.ExitCode}");
    foreach (var line in proc.StandardOutputLines)
        Console.WriteLine(line);
}
else
{
    Console.Error.WriteLine(result.ErrorOutput);
}
```

The batch overload appends every URL after the rendered options:

```csharp
var batch = await client.RunWithOptionsAsync(
    new[] { "https://...a", "https://...b", "https://...c" },
    new YtDlpOptions
    {
        VideoFormat = new YtDlpVideoFormatOptions { Format = "bestaudio" }
    });
```

### Choosing a format and merge container

```csharp
await client.DownloadAsync(
    "https://www.youtube.com/watch?v=C0DPdy98e4c",
    outputDirectory: "downloads",
    new DownloadOptions
    {
        YtDlp = new YtDlpOptions
        {
            VideoFormat = new YtDlpVideoFormatOptions
            {
                Format            = "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                MergeOutputFormat = DownloadMergeFormat.Mkv,
            }
        }
    });
```

### Audio-only download

```csharp
await client.DownloadAudioAsync(
    "https://www.youtube.com/watch?v=C0DPdy98e4c",
    outputDirectory: "downloads",
    new AudioDownloadOptions
    {
        AudioFormat = AudioConversionFormat.Mp3,
        YtDlp = new YtDlpOptions
        {
            PostProcessing = new YtDlpPostProcessingOptions
            {
                AudioQuality = "0",       // VBR best
                EmbedThumbnail = true,
                EmbedMetadata  = true,
            }
        }
    });
```

`AudioConversionFormat` covers `Best`, `Aac`, `Alac`, `Flac`, `M4a`, `Mp3`, `Opus`, `Vorbis`, `Wav`.

### Per-call convenience flags

Every download option record (`DownloadOptions`, `AudioDownloadOptions`,
`PlaylistDownloadOptions`, `AudioPlaylistDownloadOptions`, `MetadataDownloadOptions`,
`LiveChatDownloadOptions`) carries a small set of convenience flags so you do not have to drop
into `YtDlpOptions` for the most common per-call overrides:

| Flag                    | Effect                                                                           |
|-------------------------|----------------------------------------------------------------------------------|
| `AbortOnError`          | Force `--abort-on-error`; suppresses any inherited `IgnoreDownloadErrors` default. |
| `OutputTemplate`        | Set `--output` for this call only.                                                |
| `RestrictFilenames`     | `true` → `--restrict-filenames`, `false` → `--no-restrict-filenames`.             |
| `OverwriteFiles`        | `true` → `--force-overwrites`, `false` → `--no-overwrites --no-force-overwrites`. |
| `IgnoreDownloadErrors`  | `true` → `--ignore-errors`, `false` → `--abort-on-error`.                         |

```csharp
await client.DownloadAsync(
    "https://...",
    outputDirectory: "downloads",
    new DownloadOptions
    {
        OutputTemplate       = "%(id)s.%(ext)s",
        RestrictFilenames    = true,
        OverwriteFiles       = true,
        IgnoreDownloadErrors = false,    // explicit --abort-on-error this call
    });
```

The convenience flags compose on top of any `YtDlp` overrides you set on the same record.

### Output template and filename rules

```csharp
var ytDlp = new YtDlpOptions
{
    Filesystem = new YtDlpFilesystemOptions
    {
        // Standard yt-dlp template syntax.
        Output             = "%(extractor)s_%(title)s_%(upload_date)s.%(ext)s",
        RestrictFilenames  = true,
        WindowsFilenames   = true,
        TrimFilenames      = 200,
        NoOverwrites       = true,
    }
};
```

If `Filesystem.Paths` is not set explicitly, the client auto-populates it with
`home:<outputDirectory>` so the rendered template resolves under the directory you passed to
`DownloadAsync`.

### Playlists

```csharp
// All entries.
await client.DownloadPlaylistAsync(
    "https://www.youtube.com/playlist?list=PLAYLIST_ID",
    outputDirectory: "downloads",
    new PlaylistDownloadOptions
    {
        PlaylistItems = "1-5,8,10-",      // yt-dlp playlist-items selector
        YtDlp = new YtDlpOptions
        {
            VideoSelection = new YtDlpVideoSelectionOptions { MaxDownloads = 25 },
        }
    });

// Or as audio:
await client.DownloadAudioPlaylistAsync(
    "https://www.youtube.com/playlist?list=PLAYLIST_ID",
    outputDirectory: "downloads",
    new AudioPlaylistDownloadOptions
    {
        AudioFormat  = AudioConversionFormat.Opus,
        PlaylistItems = "1-10",
    });

// Stream metadata for each entry without downloading anything:
await foreach (var entry in client.GetPlaylistInfoAsync("https://...playlist..."))
{
    Console.WriteLine($"{entry.Id} - {entry.Title}");
}

// Or fetch a single shallow listing, with comments:
var shallow = await client.GetVideoInfoAsync(
    "https://...playlist...",
    flat: true,
    fetchComments: false);

Console.WriteLine($"playlist: {shallow.PlaylistTitle} ({shallow.PlaylistCount} entries)");
```

### Date and filesize filters

```csharp
var ytDlp = new YtDlpOptions
{
    VideoSelection = new YtDlpVideoSelectionOptions
    {
        Dateafter    = "20240101",   // YYYYMMDD
        Datebefore   = "20241231",
        MinFilesize  = "10M",
        MaxFilesize  = "2G",
        AgeLimit     = 18,
        DownloadArchive = "archive.txt",
    }
};
```

### Authentication, cookies, proxies

```csharp
var ytDlp = new YtDlpOptions
{
    Network = new YtDlpNetworkOptions
    {
        Proxy         = "socks5://127.0.0.1:1080",
        SocketTimeout = 15.0,
        SourceAddress = "0.0.0.0",
    },
    Authentication = new YtDlpAuthenticationOptions
    {
        Username      = "me@example.test",
        Password      = "hunter2",
        Twofactor     = "123456",
        VideoPassword = "secret",
    },
    AdvancedArguments =
    [
        // Cookies are intentionally not in the typed surface; treat as sensitive.
        new RawYtDlpArgument
        {
            Name        = "--cookies",
            Value       = "/path/to/cookies.txt",
            IsSensitive = true,
        }
    ]
};
```

### Subtitles

```csharp
var ytDlp = new YtDlpOptions
{
    Subtitle = new YtDlpSubtitleOptions
    {
        WriteSubs     = true,
        WriteAutoSubs = true,
        SubLangs      = "en,ja,es",
        SubFormat     = SubtitleFormat.Srt,
    },
    PostProcessing = new YtDlpPostProcessingOptions
    {
        ConvertSubs = SubtitleFormat.Vtt,
        EmbedSubs   = true,
    }
};
```

### Thumbnails and metadata sidecars

```csharp
// Just the metadata, no media:
await client.DownloadMetadataAsync(
    "https://...",
    "downloads",
    new MetadataDownloadOptions
    {
        WriteThumbnail    = true,
        WriteSubtitles    = true,
        SubtitleLanguages = "en,ja",
        YtDlp = new YtDlpOptions
        {
            Filesystem = new YtDlpFilesystemOptions { Output = "%(id)s.%(ext)s" }
        }
    });

// Or alongside a real download:
var ytDlp = new YtDlpOptions
{
    Thumbnail  = new YtDlpThumbnailOptions  { WriteThumbnail = true },
    Filesystem = new YtDlpFilesystemOptions { WriteInfoJson  = true, WriteDescription = true },
};
```

### Post-processing: recode, remux, embed

```csharp
var ytDlp = new YtDlpOptions
{
    PostProcessing = new YtDlpPostProcessingOptions
    {
        RemuxVideo       = VideoContainer.Mkv,            // --remux-video mkv
        RecodeVideo      = VideoRecodeFormat.Mp4,         // --recode-video mp4
        EmbedSubs        = true,
        EmbedThumbnail   = true,
        EmbedMetadata    = true,
        EmbedChapters    = true,
        FfmpegLocation   = "/usr/local/bin",              // directory or executable path
    }
};
```

### SponsorBlock

```csharp
var ytDlp = new YtDlpOptions
{
    SponsorBlock = new YtDlpSponsorBlockOptions
    {
        SponsorblockMark   = "all",
        SponsorblockRemove = "sponsor,selfpromo",
    }
};
```

### Section downloads and rate limits

```csharp
var ytDlp = new YtDlpOptions
{
    Download = new YtDlpDownloadOptions
    {
        DownloadSections    = ["*0-30", "*60-90"],   // first 30s and 60-90s
        LimitRate           = "5M",                  // bytes/sec, K/M/G allowed
        ConcurrentFragments = 4,
        Retries             = "infinite",
        FragmentRetries     = "10",
    }
};
```

### Forwarding raw stdout/stderr

For verbose console apps, plumb yt-dlp's raw output straight through:

```csharp
var opts = new YtDlpClientOptions
{
    StdoutForwardingWriter = Console.Out,
    StderrForwardingWriter = Console.Error,
};
```

Each line read from yt-dlp is mirrored to the writer in addition to being parsed/queued. This is
independent of progress reporting — you can use either, both, or neither.

### Cancellation

Every async method takes a `CancellationToken`. Cancellation requests trigger a graceful kill of
the yt-dlp child process; if the process does not exit within
`YtDlpClientOptions.TerminationGracePeriod` (default 5s), the entire process tree is force-killed.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await client.DownloadAsync(url, "downloads", ct: cts.Token);
```

### Advanced (raw) arguments

Anything not yet modelled in the typed surface — niche flags, brand-new yt-dlp features, sensitive
values you would rather not log — can be appended via `AdvancedArguments`:

```csharp
var ytDlp = new YtDlpOptions
{
    AdvancedArguments =
    [
        new RawYtDlpArgument { Name = "--no-mtime" },
        new RawYtDlpArgument { Name = "--user-agent", Value = "MyApp/1.0" },
        new RawYtDlpArgument
        {
            Name   = "--external-downloader-args",
            Value  = "aria2c:-x16 -k1M",
        },
        new RawYtDlpArgument
        {
            Name        = "--cookies-from-browser",
            Value       = "firefox",
            IsSensitive = true,    // hint for your own logging
        },
    ]
};
```

Raw arguments must start with `--` (long-form). Renderer validation throws
`YtDlpValidationException` otherwise.

### Composing options with `WithOverrides`

`YtDlpOptions.WithOverrides(...)` merges an "override" set on top of a base set, copying every
explicitly-set value (non-null strings/nullables, `true` switches, non-empty collections) and —
crucially — clearing any opposite boolean switch the override introduced. So if you turn on
`--no-overwrites` in the override, the base's `--force-overwrites` is wiped; turning on
`--restrict-filenames` wipes the base's `--no-restrict-filenames`.

```csharp
var defaults = new YtDlpOptions
{
    Filesystem = new YtDlpFilesystemOptions
    {
        ForceOverwrites = true,
        RestrictFilenames = true,
    }
};

var perCall = new YtDlpOptions
{
    Filesystem = new YtDlpFilesystemOptions
    {
        NoOverwrites         = true,   // wipes ForceOverwrites
        NoRestrictFilenames  = true,   // wipes RestrictFilenames
    }
};

var merged = defaults.WithOverrides(perCall);
// merged.Filesystem.NoOverwrites         == true
// merged.Filesystem.ForceOverwrites      == false
// merged.Filesystem.NoRestrictFilenames  == true
// merged.Filesystem.RestrictFilenames    == false
```

If you want the older behaviour that retains both opposing switches verbatim,
`OverrideOptions(...)` is still on `YtDlpOptionsExtensions` for backwards compatibility.

---

## Metadata models

`GetVideoInfoAsync` and `GetPlaylistInfoAsync` return strongly-typed `VideoInfo` records modelled
after yt-dlp's `--dump-json` output. The model covers (non-exhaustive):

- Identification: `_type` (as `MetadataType?`), `Id`, `Title`, `FullTitle`, `AltTitle`, `DisplayId`.
- Dates: `UploadDate`, computed `ParsedUploadDate`, `Timestamp`, `ReleaseTimestamp`,
  `ReleaseDate`, `ModifiedTimestamp`, `ModifiedDate`.
- Channel / uploader: `Channel`, `ChannelId`, `ChannelUrl`, `ChannelFollowerCount`, `Uploader`,
  `UploaderId`, `UploaderUrl`, `Creator`, `Creators`, `License`.
- Engagement: `ViewCount`, `LikeCount`, `DislikeCount`, `RepostCount`, `CommentCount`,
  `ConcurrentViewCount`, `AverageRating`.
- Live state: `IsLive`, `WasLive`, `LiveStatus` (typed enum: `IsLive`/`WasLive`/`IsUpcoming`/…),
  `StartTime`, `EndTime`, `Availability` (typed enum: `Public`/`Private`/`Unlisted`/…).
- Music: `Track`, `TrackNumber`, `Artist`, `Artists`, `Album`, `AlbumArtist`, `AlbumArtists`,
  `DiscNumber`, `ReleaseYear`, `Genre`.
- Playlist / container: `Entries`, `PlaylistId`, `PlaylistTitle`, `PlaylistIndex`, `PlaylistCount`,
  `Series`, `Season`, `SeasonNumber`, `Episode`, `EpisodeNumber`, `SectionTitle`, `SectionStart`,
  `SectionEnd`.
- Collections: `Formats` (`FormatInfo`), `Thumbnails` (`ThumbnailInfo`), `Chapters`
  (`ChapterInfo`), `Subtitles` and `AutomaticCaptions` (`Dictionary<string, IReadOnlyList<SubtitleTrack>>`),
  `Comments` (`CommentInfo`).
- Storyboards (`JsonElement?`) and a fallback `ExtensionData` dictionary that captures any
  extractor-specific fields the typed model does not explicitly enumerate, so new fields keep
  flowing through after a yt-dlp upgrade.

`FormatInfo` likewise carries the full set: `Url`, `FormatId`, `Format`, `FormatNote`, `Ext`,
`Resolution`, `Width`, `Height`, `Filesize`, `FilesizeApprox`, `Fps`, `Tbr`, `Vbr`, `Abr`, `Asr`,
`Vcodec`, `Acodec`, `DynamicRange`, `AudioExt`, `VideoExt`, `Protocol`, `Language`, `Quality`,
`Preference`, `PlayerUrl`, `HttpHeaders`, plus an `ExtensionData` fallback. `ThumbnailInfo` and
`SubtitleTrack` carry the same `Ext`/`Filesize`/`HttpHeaders`/`ExtensionData` shape.

```csharp
var info = await client.GetVideoInfoAsync(
    "https://...",
    fetchComments: true);

Console.WriteLine($"{info.Channel} ({info.ChannelFollowerCount:N0} followers)");
if (info.LiveStatus is LiveStatus.IsLive) Console.WriteLine("LIVE NOW");

foreach (var fmt in info.Formats ?? [])
    Console.WriteLine($"  {fmt.FormatId,-12} {fmt.Resolution,-10} {fmt.Vcodec}/{fmt.Acodec}");

foreach (var c in info.Comments ?? [])
    Console.WriteLine($"  [{c.Author}] {c.Text}");
```

---

## Progress reporting

There are two progress styles. Pick one — they are equivalent.

### Callback (`IProgress<YtDlpProgress>`)

```csharp
var progress = new Progress<YtDlpProgress>(p =>
{
    if (p.Phase == ProgressPhase.Downloading && p.Percent is { } pct)
    {
        Console.WriteLine(
            $"[download] {pct,6:F1}%   {p.Speed}   ETA {p.Eta:hh\\:mm\\:ss}");
    }
    else if (p.Phase == ProgressPhase.Finished)
    {
        Console.WriteLine($"[done] {p.Destination ?? p.Message}");
    }
});

await client.DownloadAsync(url, "downloads", progress: progress);
```

### Async stream (`IAsyncEnumerable<YtDlpProgress>`)

```csharp
await foreach (var p in client.DownloadWithProgressAsync(url, "downloads"))
{
    switch (p.Phase)
    {
        case ProgressPhase.Downloading:        /* p.Percent / p.TotalBytes / p.Speed / p.Eta */ break;
        case ProgressPhase.Finished:           /* 100% or already-on-disk; p.Destination set */ break;
        case ProgressPhase.Merging:            /* ffmpeg merging */                              break;
        case ProgressPhase.ExtractingAudio:    /* audio extraction; p.Destination set */         break;
        case ProgressPhase.Converting:         /* recoding */                                    break;
        case ProgressPhase.EmbeddingThumbnail: /* */                                             break;
        case ProgressPhase.PostProcessing:     /* */                                             break;
        case ProgressPhase.Completed:          /* */                                             break;
        case ProgressPhase.Unknown:            /* unrecognised line; p.Message has it raw */     break;
    }
}
```

> yt-dlp by default rewrites the progress line in place. If you want frequent updates, set
> `VerbositySimulation = new YtDlpVerbositySimulationOptions { Newline = true }` so each progress
> tick is its own line.

`YtDlpProgress` exposes:

| Property         | Notes                                                                          |
|------------------|--------------------------------------------------------------------------------|
| `Phase`          | One of the values above.                                                       |
| `Percent`        | 0-100, when the line was a download tick.                                      |
| `DownloadedBytes`| Computed as `Percent × TotalBytes / 100`.                                      |
| `TotalBytes`     | Parsed from `KiB`/`MiB`/`GiB`/`TiB`/`KB`/`MB`/`GB`/`TB`/`B`.                   |
| `Speed`          | Raw token, e.g. `"2.50MiB/s"`.                                                 |
| `Eta`            | **`TimeSpan?`**, parsed from `MM:SS` / `HH:MM:SS`.                             |
| `Destination`    | Output path emitted by `Destination:` lines and `… has already been downloaded`. |
| `Message`        | The post-prefix text of the line.                                              |
| `AdditionalInfo` | The same text, kept for backwards compatibility.                               |
| `RawLine`        | Original line, for diagnostics.                                                |
| `Timestamp`      | UTC time of parse.                                                             |

For ad-hoc parsing of a single line, `ProgressLineParser.TryParse(line, out var p)` returns
`false` for unrecognised lines. A `ProgressLineParser.Parse(line)` fallback always returns a
`YtDlpProgress` (using `Phase = Unknown` for unrecognised lines), which is handy when you are
piping arbitrary stdout through your own UI.

---

## Batching with the execution scheduler

Use `IYtDlpExecutionScheduler` when you have a queue of independent downloads and want to bound
concurrency:

```csharp
var scheduler = provider.GetRequiredService<IYtDlpExecutionScheduler>();

var requests = new[]
{
    // Plain video.
    new DownloadRequest
    {
        Url             = "https://example.test/a",
        OutputDirectory = "downloads",
    },

    // Audio-only.
    new DownloadRequest
    {
        Url             = "https://example.test/b",
        OutputDirectory = "downloads",
        Kind            = DownloadRequestKind.Audio,
        AudioOptions    = new AudioDownloadOptions { AudioFormat = AudioConversionFormat.Mp3 },
    },

    // Playlist with custom format.
    new DownloadRequest
    {
        Url             = "https://example.test/playlist",
        OutputDirectory = "downloads",
        Kind            = DownloadRequestKind.Playlist,
        PlaylistOptions = new PlaylistDownloadOptions
        {
            PlaylistItems = "1-10",
            YtDlp = new YtDlpOptions
            {
                VideoFormat = new YtDlpVideoFormatOptions { Format = "bestaudio" }
            }
        },
    },

    // Metadata sidecars only.
    new DownloadRequest
    {
        Url             = "https://example.test/c",
        OutputDirectory = "downloads",
        Kind            = DownloadRequestKind.Metadata,
        MetadataOptions = new MetadataDownloadOptions { WriteThumbnail = true },
    },
};

// maxConcurrency is optional; null falls back to YtDlpClientOptions.DownloadConcurrency.
await foreach (var result in scheduler.ExecuteAsync(requests, maxConcurrency: 3))
{
    if (result.Success)
        Console.WriteLine($"OK   {result.Url} (exit {result.ExitCode})");
    else
        Console.WriteLine($"FAIL {result.Url}: {result.ErrorOutput}");
}
```

`DownloadRequest.Kind` selects which `DownloadXxxAsync` method the scheduler calls and which of the
typed option records (`DownloadOptions`, `AudioOptions`, `PlaylistOptions`, `AudioPlaylistOptions`,
`MetadataOptions`, `LiveChatOptions`) is honoured. Per-request `Progress` reporters are routed
through to the underlying client call.

Concurrency comes from `YtDlpClientOptions.DownloadConcurrency` (default `2`); the optional
`maxConcurrency` argument on `ExecuteAsync` overrides it for that batch only. `ExecuteBulkAsync` is
retained as a thin alias for the no-override case. Failures of individual jobs are surfaced via
`DownloadResult.Success`/`ErrorOutput`/`Error`/`ExitCode`; they do not stop sibling jobs.

---

## Binary provisioning (yt-dlp, ffmpeg, ffprobe, Deno)

`IYtDlpBinaryDownloader` fetches the right release asset for the current OS/architecture, streams
the download with progress reporting, and atomically writes the final file (`.download` staging
file + `File.Move`). It is safe to use as a singleton.

```csharp
using YtDlpSharpLib.Provisioning;

using var downloader = new YtDlpBinaryDownloader(new YtDlpBinaryDownloaderOptions
{
    DefaultDirectory = Path.Combine(AppContext.BaseDirectory, "tools"),
});

// One-shot — grab everything you need in a single call:
var result = await downloader.DownloadAllAsync(
    new BinaryDownloadOptions
    {
        Directory       = "tools",
        SkipExisting    = true,
        DownloadYtDlp   = true,
        DownloadFfmpeg  = true,
        DownloadFfprobe = true,
        DownloadDeno    = false,
    },
    progress: new Progress<BinaryDownloadProgress>(p =>
        Console.WriteLine($"{p.Kind}: {p.BytesReceived}/{p.TotalBytes ?? 0}")));

Console.WriteLine($"yt-dlp:  {result.YtDlpPath}");
Console.WriteLine($"ffmpeg:  {result.FfmpegPath}");
Console.WriteLine($"ffprobe: {result.FfprobePath}");
```

Or fetch a single binary:

```csharp
var path = await downloader.DownloadYtDlpAsync(
    directory: "tools",
    progress:  new Progress<BinaryDownloadProgress>(/* … */));
```

Supported platforms (per binary) match what the upstream projects publish:

| Binary  | Source                                                    |
|---------|-----------------------------------------------------------|
| yt-dlp  | `https://github.com/yt-dlp/yt-dlp/releases/latest/...`    |
| ffmpeg  | `https://ffbinaries.com/api/v1/version/latest`            |
| ffprobe | `https://ffbinaries.com/api/v1/version/latest`            |
| Deno    | `https://dl.deno.land/release-latest.txt` + zip release   |

All URLs and the user-agent are overridable via `YtDlpBinaryDownloaderOptions`.

When registered via `AddYtDlpBinaryDownloader`, the downloader will prefer an `HttpClient`
provided by the container; otherwise it creates and disposes its own (subject to `HttpTimeout`).

---

## Exceptions

All exceptions thrown by the library derive from `YtDlpException`, which carries the (sanitized)
command line and exit code where applicable.

| Exception                          | When it is thrown                                                  |
|------------------------------------|--------------------------------------------------------------------|
| `YtDlpNotFoundException`           | The `yt-dlp` executable could not be located.                      |
| `YtDlpProcessException`            | The yt-dlp process exited with a non-zero exit code. Carries `LastStderrLines`. |
| `YtDlpUnavailableException`        | yt-dlp reported a known-bad video state (geo block, removed, …).   |
| `YtDlpValidationException`         | Bad option values detected before launching the process.           |
| `YtDlpParsingException`            | yt-dlp output (JSON metadata, progress) could not be parsed.       |
| `YtDlpBinaryDownloadException`     | A binary download/extract failed; exposes the offending `Url`.     |

```csharp
try
{
    await client.DownloadAsync(url, "downloads");
}
catch (YtDlpProcessException ex)
{
    Console.Error.WriteLine($"yt-dlp exited with {ex.ExitCode}: {ex.Message}");
    Console.Error.WriteLine(ex.LastStderrLines);
}
catch (YtDlpNotFoundException ex)
{
    Console.Error.WriteLine($"Install yt-dlp or set YtDlpExecutablePath. Tried: {ex.AttemptedPath}");
}
```

The `Try`-prefixed variants (`TryGetVideoInfoAsync`, `TryDownloadAsync`) and `RunWithOptionsAsync`
return a `RunResult` / `RunResult<T>` instead of throwing for `YtDlpException`-derived failures —
useful when you want to handle errors without unwinding the stack.

---

## Configuration reference (`YtDlpClientOptions`)

| Property                   | Default          | Purpose                                                                 |
|----------------------------|------------------|-------------------------------------------------------------------------|
| `YtDlpExecutablePath`      | `"yt-dlp"`       | Path to the yt-dlp binary (resolved against `PATH` if relative).        |
| `FfmpegExecutablePath`     | `"ffmpeg"`       | Path to ffmpeg, used by yt-dlp for merge/convert.                       |
| `OutputFolder`             | `null`           | Default output folder; per-call `Filesystem.Paths` wins when set.       |
| `OutputFileTemplate`       | `null`           | Default `--output` template; per-call `Filesystem.Output` wins when set.|
| `RestrictFilenames`        | `false`          | Default `--restrict-filenames`; per-call filename flags win when set.   |
| `OverwriteFiles`           | `false`          | Default `--force-overwrites`; per-call overwrite flags win when set.    |
| `IgnoreDownloadErrors`     | `false`          | Default `--ignore-errors`; per-call `AbortOnError` wins when set.       |
| `DownloadConcurrency`      | `2`              | Max concurrent downloads for `IYtDlpExecutionScheduler`.                |
| `TerminationGracePeriod`   | `5s`             | Grace given to yt-dlp after a graceful kill before force-killing tree.  |
| `StderrTailLineCount`      | `100`            | Lines of stderr retained for non-zero-exit error reporting.             |
| `StdoutForwardingWriter`   | `null`           | Mirror yt-dlp stdout to this `TextWriter` (e.g. `Console.Out`).         |
| `StderrForwardingWriter`   | `null`           | Mirror yt-dlp stderr to this `TextWriter`.                              |
| `EnvironmentVariables`     | empty            | Extra env vars passed to the yt-dlp child process.                      |

The same defaults are exposed as mutable convenience properties on `YtDlpClient`
(`OutputFolder`, `OutputFileTemplate`, `RestrictFilenames`, `OverwriteFiles`,
`IgnoreDownloadErrors`) so they can be flipped at runtime without rebuilding the options record.

For development, build, and contribution guidance — including the option-generator tooling — see
[`DEVELOPMENT.md`](./DEVELOPMENT.md).
