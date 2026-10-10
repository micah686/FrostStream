# Development guide

This document is for contributors working on **YtDlpSharpLib** itself. For consumer-facing usage,
see [`README.md`](./README.md).

---

## Repository layout

```
YtDlpSharpLib/                            # Solution root
├── YtDlpSharpLib.slnx                    # Solution file
├── GenerateOptions.cs                    # Single-file generator that emits typed option groups
├── YtDlpSharpLib/                        # The library project (net10.0)
│   ├── YtDlpClient.cs                    # Default IYtDlpClient implementation
│   ├── IYtDlpClient.cs                   # Public client surface
│   ├── YtDlpClientOptions.cs             # Client-wide configuration record
│   ├── RunResult.cs                      # Non-throwing result records (RunResult, RunResult<T>)
│   ├── ServiceCollectionExtensions.cs    # AddYtDlpClient / AddYtDlpBinaryDownloader
│   ├── Downloads/                        # High-level operation option records
│   │   ├── DownloadOptions.cs            # Base record carrying convenience flags
│   │   ├── AudioDownloadOptions.cs       # : DownloadOptions
│   │   ├── PlaylistDownloadOptions.cs    # : DownloadOptions
│   │   ├── AudioPlaylistDownloadOptions.cs # : PlaylistDownloadOptions
│   │   ├── MetadataDownloadOptions.cs    # : DownloadOptions
│   │   └── LiveChatDownloadOptions.cs    # : DownloadOptions
│   ├── Models/                           # Strongly-typed --dump-json output
│   │   ├── VideoInfo.cs / FormatInfo.cs / SubtitleTrack.cs / ThumbnailInfo.cs / ChapterInfo.cs
│   │   ├── CommentInfo.cs                # yt-dlp comments[]
│   │   ├── MetadataType.cs / LiveStatus.cs / Availability.cs
│   │   ├── MetadataJsonConverters.cs     # snake_case JSON converters for the enums above
│   │   └── YtDlpJsonContext.cs           # Source-generated System.Text.Json context
│   ├── Options/                          # Hand-written enums + attributes + root partial record
│   │   ├── YtDlpOptions.cs               # The "user-facing" half of the partial record
│   │   ├── YtDlpOptionsExtensions.cs     # OverrideOptions / WithOverrides / Add/Set/DeleteCustomOption
│   │   ├── RawYtDlpArgument.cs
│   │   ├── YtDlpArgumentAttribute.cs
│   │   ├── YtDlpOptionGroupAttribute.cs
│   │   ├── YtDlpEnumValueAttribute.cs
│   │   ├── ArgumentValueStyle.cs
│   │   ├── AudioConversionFormat.cs / SubtitleFormat.cs / VideoContainer.cs / ...
│   │   └── Generated/                    # *.g.cs emitted by GenerateOptions.cs
│   ├── Rendering/                        # YtDlpOptions  →  argv tokens
│   │   ├── IYtDlpArgumentRenderer.cs
│   │   └── YtDlpArgumentRenderer.cs
│   ├── Process/                          # Process abstraction (testable seam)
│   │   ├── IYtDlpProcess.cs / YtDlpProcess.cs
│   │   ├── IYtDlpProcessFactory.cs / YtDlpProcessFactory.cs
│   │   ├── YtDlpProcessStartInfo.cs
│   │   └── YtDlpProcessResult.cs         # Captured stdout/stderr from RunWithOptionsAsync
│   ├── Progress/                         # stdout line  →  YtDlpProgress
│   │   ├── ProgressLineParser.cs         # ReadOnlySpan<char> + string overloads, Parse fallback
│   │   ├── ProgressPhase.cs
│   │   └── YtDlpProgress.cs              # Percent / TotalBytes / Speed / TimeSpan? Eta / Destination / Message / Timestamp
│   ├── Provisioning/                     # Optional binary downloader (yt-dlp/ffmpeg/ffprobe/Deno)
│   │   ├── IYtDlpBinaryDownloader.cs / YtDlpBinaryDownloader.cs
│   │   ├── YtDlpBinaryDownloaderOptions.cs / BinaryDownloadOptions.cs
│   │   ├── BinaryDownloadResult.cs / BinaryDownloadProgress.cs / BinaryKind.cs
│   │   ├── FfBinariesResponse.cs / BinaryDownloaderJsonContext.cs
│   ├── Scheduling/                       # Concurrency-bounded batching
│   │   ├── IYtDlpExecutionScheduler.cs / YtDlpExecutionScheduler.cs
│   │   ├── DownloadRequest.cs / DownloadRequestKind.cs / DownloadResult.cs
│   ├── Exceptions/                       # YtDlpException base + 6 typed subclasses
│   └── Internal/                         # Lib-internal helpers (RingBuffer)
└── Tests/
    ├── YtDlpSharpLib.Tests/              # In-process unit tests (xUnit)
    └── YtDlpSharpLib.IntegrationTests/   # Real yt-dlp invocations (TUnit)
```

---

## Architecture at a glance

```
                ┌─────────────────────┐
caller code ──▶ │   IYtDlpClient      │  (download / metadata / chat / version / RunWithOptionsAsync)
                └──────────┬──────────┘
                           │ uses
       ┌───────────────────┼─────────────────────────┐
       ▼                   ▼                         ▼
┌──────────────┐   ┌──────────────────┐   ┌──────────────────────────┐
│ YtDlpOptions │   │ IYtDlpArgument   │   │ IYtDlpProcessFactory     │
│  (records)   │   │ Renderer         │   │   → IYtDlpProcess        │
└──────────────┘   └──────────────────┘   └──────────────────────────┘
                           │                         │
                  argv tokens                  yt-dlp child process
                           │                         │
                           ▼                         ▼
                  ┌──────────────────┐   ┌──────────────────────────┐
                  │ ProgressLine     │◀──│ stdout/stderr (Channel)  │
                  │ Parser           │   │ + RingBuffer for stderr  │
                  └──────────────────┘   └──────────────────────────┘
                           │
                           ▼
                  IProgress<YtDlpProgress> / IAsyncEnumerable
```

The library is intentionally split into single-purpose seams. Anything you might want to fake in a
test (the renderer, the process factory, the time provider) is an interface registered in
`ServiceCollectionExtensions.AddYtDlpClient`.

---

## Component responsibilities

### `YtDlpClient` (`YtDlpClient.cs`)

The orchestrator. It exposes three constructors:

- `YtDlpClient(YtDlpClientOptions? options = null)` — wires up the default `YtDlpProcessFactory`,
  `YtDlpArgumentRenderer`, and `TimeProvider.System`. Suitable for console apps and ad-hoc scripts.
- `YtDlpClient(YtDlpClientOptions, IYtDlpProcessFactory, IYtDlpArgumentRenderer, TimeProvider)` —
  the all-seams direct overload. Used by tests with fake factories.
- `YtDlpClient(IOptions<YtDlpClientOptions>, IYtDlpProcessFactory, IYtDlpArgumentRenderer, TimeProvider)` —
  forwarded to by the DI container; just unwraps `IOptions<T>.Value` and delegates to the direct
  overload.

Each `XxxAsync` download method:

1. Takes a high-level option record (`DownloadOptions`, `AudioDownloadOptions`, …).
2. Composes a base `YtDlpOptions` from `_defaultYtDlpOptions` (built from `YtDlpClientOptions`)
   plus the per-call `YtDlp` overrides via `ComposeDefaultOptions`.
3. Calls `ApplyDownloadDefaults` to inject `Filesystem.Paths = "home:<outputDirectory>"` if the
   caller did not set it.
4. Calls `ApplyConvenienceFlags` to honour the per-call `AbortOnError`, `OutputTemplate`,
   `RestrictFilenames`, `OverwriteFiles`, and `IgnoreDownloadErrors` flags carried on
   `DownloadOptions`.
5. Mutates the underlying `YtDlpOptions` for the operation kind (e.g. audio extraction sets
   `PostProcessing.ExtractAudio = true` and `AudioFormat`).
6. Builds a `YtDlpProcessStartInfo` via the renderer, hands it to `IYtDlpProcessFactory.Create`,
   and runs the child via `RunInternalAsync` or `StreamStdoutAsync`.
7. On non-zero exit, throws `YtDlpProcessException` with the last `StderrTailLineCount` lines of
   stderr, retained in the lock-free `Internal/RingBuffer`.

`RunWithOptionsAsync(...)` (and the sync-named `RunWithOptions(...)` aliases) take the lower-level
path: they render the supplied options verbatim, append URL(s), and capture both stdout and stderr
in full via `RunCapturingAsync`. The captured `YtDlpProcessResult` is wrapped in
`RunResult<YtDlpProcessResult>`; `YtDlpException`s are caught and folded into
`RunResult.Failed(...)` rather than being thrown.

`GetVideoInfoAsync` / `TryGetVideoInfoAsync` accept three optional knobs beyond the URL/cancellation
token: `flat` (adds `--flat-playlist`), `fetchComments` (adds `--write-comments`), and
`overrideOptions` (renders an additional `YtDlpOptions` and splices the resulting argv in before
the URL). `Try`-prefixed methods return `RunResult<T>` instead of throwing.

### `YtDlpOptions` and the generated groups

`YtDlpOptions` is a `partial record`. The hand-written half (`Options/YtDlpOptions.cs`) contains
only `AdvancedArguments`. The generated half (`Options/Generated/YtDlpOptions.Generated.g.cs`)
exposes one `init`-only property per yt-dlp help section (`General`, `Network`, `VideoFormat`, …),
each typed as a generated record (`YtDlpGeneralOptions`, `YtDlpNetworkOptions`, …).

Each generated option property is decorated with `[YtDlpArgument(...)]` carrying:

- `Name`: the canonical `--long-flag`.
- `ValueStyle`: `Switch` (no value token, only emit when `true`) vs `SeparateToken` (default).
- `AllowMultiple`: emit the flag once per element of the collection value.
- `ValueTokenCount`: how many tokens follow the flag (e.g. `--alias` takes two).
- `Aliases`, `ValueName`, `Description`: documentation only (and used by `WithOverrides` to detect
  opposite switches).

Each option group is decorated with `[YtDlpOptionGroup(<order>)]`. The renderer uses `Order` (and
`MetadataToken` as the deterministic tiebreak) to walk groups in a stable order.

### `YtDlpOptionsExtensions` (`Options/YtDlpOptionsExtensions.cs`)

Holds the composition helpers users call on `YtDlpOptions`:

- `OverrideOptions(base, override, forceOverride = false)` — copies every set value from the
  override onto the base. With `forceOverride = false` (default), unset values are skipped (null
  strings/nullables, `false` booleans, empty collections). With `forceOverride = true`, every
  value from the override is copied verbatim.
- `WithOverrides(defaults, overrides)` — same merge semantics, but additionally clears any
  *opposite* boolean switch the override turned on. The opposition table is derived from the
  `[YtDlpArgument]` `Name`/`Aliases` metadata: setting `--no-foo` wipes a default `--foo` (or
  `--yes-foo`); setting `--yes-foo` wipes a default `--no-foo`; setting `--foo` wipes a default
  `--no-foo`. Use this when the per-call override is meant to *replace* a default switch rather
  than coexist with it.
- `AddCustomOption<T>` / `SetCustomOption<T>` / `DeleteCustomOption` — typed helpers for the
  `AdvancedArguments` raw-flag list. `Set` replaces existing entries with the same name; `Add`
  appends; `Delete` removes.
- `OptionComparer` — value-based equality for `YtDlpOptions` that compares sequence contents
  (handy for tests).

### `YtDlpArgumentRenderer` (`Rendering/YtDlpArgumentRenderer.cs`)

Reflection-driven. It walks groups in `[YtDlpOptionGroup]` order, walks each property in
`MetadataToken` order, and emits argv tokens according to the property's `[YtDlpArgument]`
metadata.

Rendering rules:

- `null` value → skip (so unset options never appear).
- `Switch` style → emit the flag only when value is `true`.
- `IEnumerable` (not `string`) and `ValueTokenCount == 1` → emit `--flag <item>` per item.
- `ValueTokenCount > 1` → emit `--flag <t1> <t2> …`. With `AllowMultiple = true` the value must be
  a sequence-of-sequences and the flag is repeated.
- Enums use `[YtDlpEnumValue(...)]` to emit the exact lower-case token (e.g. `mp3`, `live_chat`).
- Numbers are formatted with `CultureInfo.InvariantCulture` (so `1.25` is always `"1.25"`).
- After the typed groups, raw `AdvancedArguments` are appended verbatim. Each raw argument must
  begin with `--` or rendering throws `YtDlpValidationException`.

### `IYtDlpProcess` / `YtDlpProcess` (`Process/`)

A thin wrapper around `System.Diagnostics.Process`:

- stdout and stderr are surfaced as bounded `Channel<string>` (capacity 256, single-writer).
  Bounded mode means the process naturally back-pressures if the consumer falls behind.
- `StartAsync` translates Win32 launch failures into `YtDlpNotFoundException`.
- `Kill(entireProcessTree)` is best-effort and swallows races (process exited between the check
  and the kill).
- Optional `RawStdoutWriter`/`RawStderrWriter` mirrors every line to a `TextWriter` for verbose
  logging without disturbing the channel readers.

`IYtDlpProcessFactory` exists purely so the client can be tested with a fake that produces
canned output — see `Tests/YtDlpSharpLib.Tests/YtDlpClientTests.cs` for the pattern.

`YtDlpProcessResult` (`Process/YtDlpProcessResult.cs`) is the captured-output record returned by
`RunWithOptionsAsync`: `ExitCode`, full `StandardOutput`/`StandardError` strings, plus the per-line
`StandardOutputLines`/`StandardErrorLines` collections.

### `ProgressLineParser` (`Progress/ProgressLineParser.cs`)

Allocation-conscious `ReadOnlySpan<char>`-based parser. It looks at the leading `[tag]` token to
classify the line as `Downloading`, `Merging`, `ExtractingAudio`, `Converting`,
`EmbeddingThumbnail`, `PostProcessing`, `Finished`, `Completed`, or `Unknown`.

Download lines are further parsed for `percent`, `total bytes` (with
`KiB`/`MiB`/`GiB`/`TiB`/`KB`/`MB`/`GB`/`TB`/`B` units), `speed`, and `ETA`. The ETA is converted
to a `TimeSpan?` (`MM:SS` or `HH:MM:SS`). Lines that match `… has already been downloaded` or
that hit `100%` are surfaced as `Phase = Finished` with the destination path extracted. The
`Destination:` line emitted by `[download]` and `[ExtractAudio]` populates `YtDlpProgress.Destination`.

Two entry points are exposed:

- `TryParse(line, out var progress)` — returns `false` for unrecognised lines (preferred
  inside the client's progress fan-out).
- `Parse(line)` — never returns null/false; unrecognised lines come back as
  `Phase = Unknown` with `Message = line`. Useful for "give me a `YtDlpProgress` for this line no
  matter what" callers.

> Reminder: yt-dlp by default rewrites the progress line in place (no newline). To get a stream of
> per-tick progress events, callers should set `VerbositySimulation.Newline = true`.

### `YtDlpExecutionScheduler` (`Scheduling/`)

A `SemaphoreSlim` sized from `YtDlpClientOptions.DownloadConcurrency` (clamped to `>= 1`). Single
downloads route through `_client.DownloadAsync`. Bulk jobs are pushed in via `Task.WhenEach`, with
per-job exceptions captured into `DownloadResult.ErrorOutput`/`Error`/`ExitCode` so a failing job
does not poison its siblings. `OperationCanceledException` is rethrown rather than swallowed.

`ExecuteAsync(IEnumerable<DownloadRequest>, int? maxConcurrency = null, CancellationToken)` accepts
an optional per-call concurrency override; passing a value spins up a dedicated `SemaphoreSlim` for
that batch instead of contending against the shared one. `ExecuteBulkAsync` is preserved as a
thin wrapper that calls `ExecuteAsync(..., maxConcurrency: null, ...)`.

`DownloadRequest.Kind` (a `DownloadRequestKind` enum) selects which client method the scheduler
calls and which of the typed option records is honoured:

| Kind             | Method called on `IYtDlpClient`     | Options property used        |
|------------------|-------------------------------------|------------------------------|
| `Video`          | `DownloadAsync`                     | `DownloadOptions`            |
| `Audio`          | `DownloadAudioAsync`                | `AudioOptions`               |
| `Playlist`       | `DownloadPlaylistAsync`             | `PlaylistOptions`            |
| `AudioPlaylist`  | `DownloadAudioPlaylistAsync`        | `AudioPlaylistOptions`       |
| `Metadata`       | `DownloadMetadataAsync`             | `MetadataOptions`            |
| `LiveChat`       | `DownloadLiveChatAsync`             | `LiveChatOptions`            |

`DownloadRequest.Options` is preserved as an alias for `DownloadOptions` (backwards
compatibility); `DownloadResult.Url` is preserved as a shorthand for `Request.Url`.

### `YtDlpBinaryDownloader` (`Provisioning/`)

OS/architecture-aware download orchestration:

- **yt-dlp**: pulls the right release asset name (`yt-dlp.exe`, `yt-dlp_macos`, `yt-dlp_linux`,
  `yt-dlp_linux_aarch64`, …) from `releases/latest/download/`.
- **ffmpeg / ffprobe**: queries the ffbinaries API for the platform-specific zip URL, downloads
  it into a `MemoryStream`, and extracts the matching entry.
- **Deno**: reads the latest version string from `dl.deno.land/release-latest.txt`, formats the
  download URL with `DenoDownloadUrlTemplate`, and unzips the matching binary.

All file writes are atomic (write to `<dest>.download`, then `File.Move(..., overwrite: true)`).
On non-Windows hosts the executable bit is best-effort applied via `File.SetUnixFileMode`.

`DownloadAllAsync` honours `SkipExisting` per binary by checking the *expected* file name for the
current platform — so a second call after the first is a near no-op when nothing has changed.

### Exceptions (`Exceptions/`)

```
YtDlpException (abstract)
├── YtDlpNotFoundException        — executable not on PATH / wrong path
├── YtDlpProcessException         — yt-dlp exited non-zero (carries LastStderrLines)
├── YtDlpUnavailableException     — known-bad video (geo block, removed, …)
├── YtDlpValidationException      — option/value validation failed before launch
├── YtDlpParsingException         — JSON metadata or progress couldn't be parsed
└── YtDlpBinaryDownloadException  — provisioning failure (carries the offending URL)
```

`YtDlpException` carries the (sanitized) command line and exit code so callers always see useful
context regardless of the specific subclass.

### Metadata models (`Models/`)

The metadata records are designed to track yt-dlp's `--dump-json` output without forcing the
library to ship a code change every time an extractor adds a new field:

- `VideoInfo`, `FormatInfo`, `ThumbnailInfo`, `SubtitleTrack`, `ChapterInfo`, `CommentInfo` are
  flat `sealed record`s with `init` properties for the well-known fields, plus an `ExtensionData`
  property (`{ get; set; }`, intentionally not `init` — see [JSON note](#json-source-generation-note))
  that catches everything unmodelled. Forward-compatibility is therefore "lossless by default".
- `MetadataType`, `LiveStatus`, `Availability` are typed enums backed by hand-written
  `JsonConverter<T>`s in `MetadataJsonConverters.cs`. Each converter maps yt-dlp's snake_case
  string values onto `enum` members and rejects unknown values to `Unknown` rather than throwing.
- `YtDlpJsonContext` is a source-generated `JsonSerializerContext` (Metadata mode) that registers
  every record above plus `List<VideoInfo>` (used by playlist streaming).

#### JSON source-generation note

`[JsonExtensionData]` properties on records use `{ get; set; }` rather than `{ get; init; }`.
That is deliberate: when the source generator sees other `init` properties on the type, it emits a
parameterised constructor and `IsMemberInitializer = true` for each `init` property. An
`init`-only `[JsonExtensionData]` property gets caught up in the same logic and the runtime then
refuses the type with *"the extension data property cannot bind with a parameter in the
deserialization constructor."* Plain `set;` keeps the generator happy without changing observable
behaviour for callers (the property is still `IDictionary<string, JsonElement>?` and is populated
during deserialization).

---

## End-to-end request flow

A `client.DownloadAsync(url, "downloads", options)` call walks roughly this path:

1. `YtDlpClient.DownloadAsync` resolves the typed `YtDlpOptions`, applies `Paths` defaults, runs
   `ApplyConvenienceFlags`, and delegates to `RunDownloadAsync`.
2. `RunDownloadAsync` calls `BuildDownloadStartInfo`, which:
   - Asks `IYtDlpArgumentRenderer.Render(options)` for argv tokens.
   - Appends the URL.
   - Returns a `YtDlpProcessStartInfo` with executable, arguments, `WorkingDirectory =
     outputDirectory`, environment overrides, and the optional raw stdout/stderr sinks.
3. `RunInternalAsync` creates the `IYtDlpProcess`, starts background tasks that pump stdout and
   stderr from the channels (stderr is fed into a `RingBuffer<string>` of size
   `StderrTailLineCount`), then `StartAsync`s the process.
4. While the process runs, each stdout line is handed to the optional handler — for downloads,
   that handler runs `ProgressLineParser.TryParse` and reports a `YtDlpProgress` to
   `IProgress<YtDlpProgress>`.
5. On exit:
   - Exit code `0` → return.
   - Anything else → throw `YtDlpProcessException` with `command`, `exitCode`, and the captured
     stderr tail.
6. On cancellation, the process is sent a graceful kill; if it has not exited within
   `TerminationGracePeriod`, the entire process tree is force-killed.

The `DownloadWithProgressAsync` variant follows the same flow but yields parsed `YtDlpProgress`
items as they arrive instead of using a callback.

`RunWithOptionsAsync` swaps out `RunInternalAsync` for `RunCapturingAsync`, which buffers the
entire stdout/stderr stream into per-line lists, applies the optional `IProgress<YtDlpProgress>`
sink as it goes, and returns a `YtDlpProcessResult` carrying both the joined strings and the line
collections. The final wrap into `RunResult<YtDlpProcessResult>` happens at the top of the public
method so process failures become `Failed(...)` instead of thrown exceptions.

---

## Adding or modifying options

There are three places typed options live:

1. **Generated option groups** (`Options/Generated/*.g.cs`) — produced by the `GenerateOptions.cs`
   single-file program from a real `yt-dlp --help` snapshot. Do **not** hand-edit these files.
2. **Hand-written enums** (`Options/AudioConversionFormat.cs`, `SubtitleFormat.cs`,
   `VideoContainer.cs`, `DownloadMergeFormat.cs`, `VideoRecodeFormat.cs`) — surfaced for
   options the generator delegates to via `CSharpEmitter.ManualOptionTypes`.
3. **Hand-written parts of `YtDlpOptions`** (`Options/YtDlpOptions.cs`) — only
   `AdvancedArguments` lives here today.

### Regenerating the typed surface after a yt-dlp upgrade

The generator is a [C# 12 file-based program](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/sdk#run-c-files-with-dotnet-run)
(`GenerateOptions.cs`) at the repo root. Run it directly — no project file needed:

```bash
# Use yt-dlp from PATH and write straight into the library.
dotnet run GenerateOptions.cs

# Or, against a captured help snapshot for reproducibility:
yt-dlp --help > yt-dlp-help.txt
dotnet run GenerateOptions.cs -- --help-file yt-dlp-help.txt

# Alternate output directory (for diffing):
dotnet run GenerateOptions.cs -- --output-dir /tmp/generated --dry-run

# CI guard — fails if the committed files drift from the latest help text:
dotnet run GenerateOptions.cs -- --check
```

What the generator does:

- Calls `yt-dlp --help` (or reads `--help-file`) and parses the output into sections + options.
- For each option, infers a property type:
  - No value → `bool` switch.
  - Multi-token value → `IReadOnlyList<string>` (or list-of-lists when `AllowMultiple`).
  - "One of" / "Supported values:" enumerations → a generated `enum` with
    `[YtDlpEnumValue(...)]` per member.
  - Numeric value names (`SECONDS`, `N`, `NUMBER`, …) → `double`/`int`.
  - Anything else → `string`.
- Emits one file per group plus the root partial (`YtDlpOptions.Generated.g.cs`).
- Cleans up stale `*.g.cs` files in the output directory.

If you need an option to take a richer enum than the generator can infer (e.g. `--audio-format`
→ `AudioConversionFormat`), add it to the `ManualOptionTypes` map inside
`GenerateOptions.cs::CSharpEmitter` and rerun. Then add or extend the enum under `Options/`.

### Adding a high-level operation

To add another operation alongside `DownloadAudioAsync` / `DownloadPlaylistAsync`:

1. Add a record under `Downloads/` that **inherits from `DownloadOptions`** so it picks up the
   shared `YtDlp` knob and the `AbortOnError` / `OutputTemplate` / `RestrictFilenames` /
   `OverwriteFiles` / `IgnoreDownloadErrors` convenience flags for free.
2. Add a method to `IYtDlpClient` and implement it in `YtDlpClient`. Reuse
   `ComposeDefaultOptions` → `ApplyDownloadDefaults` → `ApplyConvenienceFlags` → `RunDownloadAsync`
   / `RunInternalAsync` / `StreamStdoutAsync` rather than rolling new process plumbing.
3. Apply the operation's defaults via a `YtDlpOptions with { ... }` expression so callers can
   still override anything they need.
4. If the operation should be schedulable, add a new `DownloadRequestKind` value, a per-kind
   options property on `DownloadRequest`, and an arm in `YtDlpExecutionScheduler.DispatchAsync`.
5. Add a unit test under `Tests/YtDlpSharpLib.Tests/` that asserts the rendered argv (use the
   `FakeProcessFactory` pattern in `YtDlpClientTests.cs`).
6. Add an integration test under `Tests/YtDlpSharpLib.IntegrationTests/` that runs against the
   real `yt-dlp` and asserts on the produced files.

### Adding a new metadata field

`Models/VideoInfo.cs` (and the format/comment/thumbnail/subtitle/chapter siblings) are flat
`sealed record`s — no inheritance. To surface a new field:

1. Add an `init` property in the right record. Use `JsonPropertyName` for any name that is not a
   straight snake_case ↔ PascalCase conversion (the source generator's
   `PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower` covers the common case).
2. If the field is a closed-set enum, add a typed enum under `Models/` plus a `JsonConverter<T>`
   in `MetadataJsonConverters.cs`. Map unknown strings to a `.Unknown` member rather than
   throwing.
3. Do **not** put `[JsonExtensionData]` on a property with `init`. Use `{ get; set; }` — see the
   [JSON source-generation note](#json-source-generation-note).
4. The source generator picks up the new property automatically from
   `[JsonSerializable(typeof(VideoInfo))]` etc. in `YtDlpJsonContext.cs`. Add a new
   `[JsonSerializable(...)]` line only if you introduce an entirely new top-level record.

---

## Testing

Two test projects live under `Tests/`:

### `YtDlpSharpLib.Tests` (unit, xUnit)

In-process. Uses `FakeYtDlpProcess` / `FakeProcessFactory` to feed canned stdout into the client
and assert on the argv that would have been sent to yt-dlp. Run them with:

```bash
dotnet test Tests/YtDlpSharpLib.Tests
```

### `YtDlpSharpLib.IntegrationTests` (TUnit)

Spawns the real `yt-dlp` against pinned URLs (small Vimeo + tiny YouTube test video). The
`IntegrationTestEnvironment` resolves yt-dlp and ffmpeg in this order:

1. `YTDLP_INTEGRATION_YTDLP_PATH` / `YTDLP_INTEGRATION_FFMPEG_PATH` env vars.
2. `yt-dlp` / `ffmpeg` from `PATH`.
3. `IYtDlpBinaryDownloader.DownloadAllAsync` into a temp directory (network required).

If none of these succeed, the suite calls `Skip.Test` rather than failing.

```bash
# Use whatever is on PATH:
dotnet test Tests/YtDlpSharpLib.IntegrationTests

# Pin to specific binaries (recommended on CI):
YTDLP_INTEGRATION_YTDLP_PATH=/opt/yt-dlp \
YTDLP_INTEGRATION_FFMPEG_PATH=/opt/ffmpeg \
    dotnet test Tests/YtDlpSharpLib.IntegrationTests
```

Integration tests are marked `[Category("Integration")]` and `[NotInParallel]` to keep network
contention and yt-dlp rate-limiting predictable.

---

## Build, lint, package

```bash
dotnet build YtDlpSharpLib.slnx
dotnet test  YtDlpSharpLib.slnx
dotnet pack  YtDlpSharpLib/YtDlpSharpLib.csproj -c Release
```

Project settings of note (`YtDlpSharpLib.csproj`):

- `TreatWarningsAsErrors = true`. Don't suppress warnings; fix them or scope a `#pragma` with a
  comment explaining why.
- `EnforceCodeStyleInBuild = true` and `AnalysisLevel = latest`. The latest .NET analyzers run
  on every build.
- `Nullable = enable`, `ImplicitUsings = enable`, `LangVersion = latest`.
- `DebugType = embedded`, `Deterministic = true` for reproducible packages.

The package id is `YtDlpSharpLib` (`<IsPackable>true</IsPackable>`).

---

## Conventions worth keeping

- **Records, init-only properties, `with`-expressions.** The whole option model is immutable; the
  client builds derived options via `with { ... }` so callers can always trust that the record
  they passed in was not mutated. The single exception is `[JsonExtensionData]` properties — see
  the [JSON note](#json-source-generation-note).
- **No blocking calls in the public surface.** Everything is async; cancellation tokens flow.
- **Channel-based output.** `IYtDlpProcess` exposes bounded channels so back-pressure is natural
  and there are no unbounded queues hiding leaks.
- **Deterministic argv ordering.** Tests assert on exact argv. The renderer uses
  `[YtDlpOptionGroup(order)]` plus `MetadataToken` as a stable tiebreak so generated changes do
  not silently re-order the command line.
- **Two-tier composition.** `OverrideOptions` is the conservative merge (kept for backwards
  compat); `WithOverrides` is the modern merge that also clears opposite switches. New
  client-internal merges should use `WithOverrides`.
- **Minimal hand-written code in `Options/`.** If you find yourself editing `*.g.cs`, regenerate
  instead — and if the generator can't express what you need, extend the generator.

---

## Common dev tasks (cheat sheet)

| Task                                       | How                                                                  |
|--------------------------------------------|----------------------------------------------------------------------|
| Update typed options to a new yt-dlp       | `dotnet run GenerateOptions.cs`                                      |
| Verify generated files are committed       | `dotnet run GenerateOptions.cs -- --check`                           |
| Run all tests                              | `dotnet test YtDlpSharpLib.slnx`                                     |
| Run only unit tests                        | `dotnet test Tests/YtDlpSharpLib.Tests`                              |
| Run integration tests against pinned bins  | set `YTDLP_INTEGRATION_*` and `dotnet test Tests/YtDlpSharpLib.IntegrationTests` |
| Build a NuGet                              | `dotnet pack YtDlpSharpLib/YtDlpSharpLib.csproj -c Release`          |
| Add a new high-level operation             | Record under `Downloads/` (inherits `DownloadOptions`), method on `IYtDlpClient`, impl + tests; add a `DownloadRequestKind` if it should be schedulable |
| Add a new typed enum (e.g. `--my-flag`)    | Add enum under `Options/`, register in `GenerateOptions.cs::ManualOptionTypes`, regenerate |
| Add a new metadata field                   | New `init` property on the relevant record; add a `JsonConverter<T>` if it is a closed-set enum |
