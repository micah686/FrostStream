using System.Globalization;
using System.Text.Json;
using NodaTime;
using Shared.Metadata;
using YtDlpSharpLib.Models;

namespace Worker.Metadata;

internal static class YtDlpMetadataMapper
{
    public static CapturedMediaMetadata Map(VideoInfo info, string platform, IClock clock)
    {
        var scrapedAt = clock.GetCurrentInstant();
        var externalMediaId = FirstNonBlank(info.Id, info.DisplayId);

        var account = new CapturedAccountMetadata
        {
            Platform = platform,
            AccountName = FirstNonBlank(info.Uploader, info.Channel, info.Creator) ?? "unknown",
            AccountHandle = FirstNonBlank(info.UploaderId, info.ChannelId, info.Uploader, info.Channel)
                ?? UnknownAccountHandle("media", externalMediaId),
            AccountUrl = FirstNonBlank(info.UploaderUrl, info.ChannelUrl),
            FollowerCount = info.ChannelFollowerCount,
            Description = null
        };

        return new CapturedMediaMetadata
        {
            Account = account,
            Media = new CapturedMediaMetadataCore
            {
                ExternalMediaId = externalMediaId,
                MetadataScrapeDate = scrapedAt,
                // Durable thumbnail/caption blob paths are filled in by the download saga once the
                // sidecar files are uploaded. The mapper no longer stores remote URLs / inline data
                // here (they aren't resolvable storage paths).
                ThumbnailStoragePath = null,
                AgeLimit = info.AgeLimit,
                AverageRating = info.AverageRating,
                LikeCount = info.LikeCount,
                DislikeCount = info.DislikeCount,
                DurationSeconds = info.Duration,
                Description = info.Description,
                ReleaseDate = ResolveReleaseDate(info),
                Title = FirstNonBlank(info.Title, info.FullTitle, info.AltTitle),
                WasLive = info.WasLive ?? info.IsLive ?? false,
                WebpageUrl = info.WebpageUrl,
                ViewCount = info.ViewCount,
                CommentCount = info.CommentCount,
                Availability = MapAvailability(info.Availability),
                Location = info.Location
            },
            Technical = BuildTechnical(info),
            // Captions are persisted from the actual downloaded subtitle blobs (bounded to the
            // requested SubLangs), not from the 100+ remote tracks yt-dlp lists in metadata.
            Captions = [],
            Comments = BuildComments(info, platform, externalMediaId, scrapedAt),
            Series = BuildSeries(info),
            Music = BuildMusic(info),
            Artists = DistinctNonBlank(info.Artists ?? ToList(info.Artist)),
            AlbumArtists = DistinctNonBlank(info.AlbumArtists ?? ToList(info.AlbumArtist)),
            Genres = DistinctNonBlank(ToList(info.Genre)),
            Tags = DistinctNonBlank(info.Tags),
            Categories = DistinctNonBlank(info.Categories),
            Cast = DistinctNonBlank(info.Cast)
        };
    }

    private static CapturedMediaTechnicalMetadata BuildTechnical(VideoInfo info)
    {
        var durationTicks = SecondsToTicks(info.Duration);
        var streams = BuildStreams(info, durationTicks);

        return new CapturedMediaTechnicalMetadata
        {
            DurationTicks = durationTicks,
            Format = new CapturedFormatMetadata
            {
                DurationTicks = durationTicks,
                StartTimeTicks = 0,
                FormatLongNames = FirstNonBlank(info.Format, info.Extension) ?? "",
                StreamCount = streams.Count,
                BitRate = 0
            },
            Streams = streams,
            Chapters = info.Chapters?
                .Select(c => new CapturedChapterMetadata
                {
                    Title = FirstNonBlank(c.Title) ?? "Chapter",
                    StartTicks = SecondsToTicks(c.StartTime),
                    EndTicks = SecondsToTicks(c.EndTime)
                })
                .ToArray() ?? []
        };
    }

    // ── streams ─────────────────────────────────────────────────────────────
    // yt-dlp's --dump-json gives format-level (not ffprobe-level) detail, so the
    // streams we synthesise here come from the format(s) yt-dlp actually selected
    // (VideoInfo.FormatId, e.g. "137+140" for a merged video+audio download).
    // Fields ffprobe would supply but yt-dlp does not — codec long name, bit depth,
    // colour space/transfer/primaries, channel layout — are left at their schema
    // defaults rather than guessed.

    private static IReadOnlyList<CapturedStreamMetadata> BuildStreams(VideoInfo info, long durationTicks)
    {
        var formats = info.Formats;
        if (formats is null || formats.Count == 0)
            return [];

        var selected = SelectFormats(info, formats);
        if (selected.Count == 0)
            return [];

        var streams = new List<CapturedStreamMetadata>();
        var videoMarked = false;
        var audioMarked = false;

        foreach (var format in selected)
        {
            if (HasVideo(format))
            {
                streams.Add(new CapturedStreamMetadata
                {
                    StreamType = "video",
                    IsPrimary = !videoMarked,
                    CodecName = FirstNonBlank(format.Vcodec) ?? "unknown",
                    CodecLongName = FirstNonBlank(format.Vcodec) ?? "unknown",
                    BitRate = KbpsToBitsPerSecond(format.Vbr ?? (HasAudio(format) ? null : format.Tbr)),
                    BitDepth = null,
                    DurationTicks = durationTicks,
                    Language = null,
                    Video = new CapturedVideoStreamMetadata
                    {
                        AvgFrameRate = format.Fps ?? 0,
                        Width = format.Width ?? ParseResolutionPart(format.Resolution, 0),
                        Height = format.Height ?? ParseResolutionPart(format.Resolution, 1),
                        HdrType = MapHdrType(format.DynamicRange)
                    }
                });
                videoMarked = true;
            }

            if (HasAudio(format))
            {
                streams.Add(new CapturedStreamMetadata
                {
                    StreamType = "audio",
                    IsPrimary = !audioMarked,
                    CodecName = FirstNonBlank(format.Acodec) ?? "unknown",
                    CodecLongName = FirstNonBlank(format.Acodec) ?? "unknown",
                    BitRate = KbpsToBitsPerSecond(format.Abr ?? (HasVideo(format) ? null : format.Tbr)),
                    BitDepth = null,
                    DurationTicks = durationTicks,
                    Language = FirstNonBlank(format.Language),
                    Audio = new CapturedAudioStreamMetadata
                    {
                        Channels = ReadAudioChannels(format),
                        SampleRateHz = format.Asr ?? 0
                    }
                });
                audioMarked = true;
            }
        }

        return streams;
    }

    private static IReadOnlyList<FormatInfo> SelectFormats(VideoInfo info, IReadOnlyList<FormatInfo> formats)
    {
        if (!string.IsNullOrWhiteSpace(info.FormatId))
        {
            var matched = info.FormatId
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => formats.FirstOrDefault(f => string.Equals(f.FormatId, id, StringComparison.Ordinal)))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToArray();

            if (matched.Length > 0)
                return matched;
        }

        // No explicit selection (e.g. a flat playlist entry) — approximate with the
        // best available video and the best stand-alone audio.
        var bestVideo = formats
            .Where(HasVideo)
            .OrderByDescending(f => (long)(f.Width ?? 0) * (f.Height ?? 0))
            .ThenByDescending(f => f.Tbr ?? 0)
            .FirstOrDefault();

        var bestAudio = formats
            .Where(f => HasAudio(f) && !HasVideo(f))
            .OrderByDescending(f => f.Abr ?? f.Tbr ?? 0)
            .FirstOrDefault();

        var fallback = new List<FormatInfo>();
        if (bestVideo is not null)
            fallback.Add(bestVideo);
        if (bestAudio is not null && !ReferenceEquals(bestAudio, bestVideo))
            fallback.Add(bestAudio);

        return fallback;
    }

    private static bool HasVideo(FormatInfo format)
        => (!string.IsNullOrWhiteSpace(format.Vcodec) && !IsNone(format.Vcodec))
            || format.Width is > 0
            || format.Height is > 0;

    private static bool HasAudio(FormatInfo format)
        => (!string.IsNullOrWhiteSpace(format.Acodec) && !IsNone(format.Acodec))
            || format.Asr is > 0
            || format.Abr is > 0;

    private static bool IsNone(string? codec)
        => string.Equals(codec, "none", StringComparison.OrdinalIgnoreCase);

    private static long KbpsToBitsPerSecond(double? kbps)
        => kbps is null or <= 0 ? 0 : (long)Math.Round(kbps.Value * 1000);

    private static string MapHdrType(string? dynamicRange)
    {
        if (string.IsNullOrWhiteSpace(dynamicRange))
            return "SDR";

        var trimmed = dynamicRange.Trim();
        // metadata.video_stream_details.hdr_type is varchar(20).
        return trimmed.Length <= 20 ? trimmed : trimmed[..20];
    }

    private static int ParseResolutionPart(string? resolution, int index)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return 0;

        var parts = resolution.Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static int ReadAudioChannels(FormatInfo format)
        => format.ExtensionData is not null
            && format.ExtensionData.TryGetValue("audio_channels", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var channels)
            && channels > 0
            ? channels
            : 0;

    private static IReadOnlyList<CapturedCommentMetadata> BuildComments(
        VideoInfo info,
        string platform,
        string? externalMediaId,
        Instant fallbackTime)
        => info.Comments?
            .Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Text))
            .Select(c => new CapturedCommentMetadata
            {
                CommentId = c.Id!,
                ParentCommentId = c.Parent,
                Text = c.Text!,
                Account = new CapturedAccountMetadata
                {
                    Platform = platform,
                    AccountName = FirstNonBlank(c.Author, c.AuthorId) ?? "unknown",
                    AccountHandle = FirstNonBlank(c.AuthorId, c.Author)
                        ?? UnknownAccountHandle("comment", externalMediaId, c.Id),
                    AccountUrl = null,
                    Description = null
                },
                CommentTimestamp = c.Timestamp is { } ts ? Instant.FromUnixTimeSeconds(ts) : fallbackTime,
                LikeCount = ToInt32(c.LikeCount),
                DislikeCount = ToInt32(c.DislikeCount),
                IsFavorited = c.IsFavorited ?? false,
                IsPinned = c.IsPinned ?? false
            })
            .ToArray() ?? [];

    private static CapturedSeriesMetadata? BuildSeries(VideoInfo info)
        => string.IsNullOrWhiteSpace(info.Series)
            ? null
            : new CapturedSeriesMetadata
            {
                SeriesName = info.Series!,
                SeasonCount = null,
                SeasonNumber = info.SeasonNumber ?? 1,
                SeasonName = info.Season,
                EpisodeNumber = info.EpisodeNumber ?? 1,
                EpisodeName = FirstNonBlank(info.Episode, info.Title, info.FullTitle) ?? "unknown"
            };

    private static CapturedMusicMetadata? BuildMusic(VideoInfo info)
        => string.IsNullOrWhiteSpace(info.Album) && string.IsNullOrWhiteSpace(info.Track)
            ? null
            : new CapturedMusicMetadata
            {
                AlbumTitle = FirstNonBlank(info.Album, info.Title) ?? "unknown",
                AlbumType = info.AlbumType,
                DiscNumber = info.DiscNumber,
                ReleaseYear = info.ReleaseYear,
                TrackTitle = FirstNonBlank(info.Track, info.Title, info.FullTitle) ?? "unknown",
                TrackNumber = info.TrackNumber ?? 0,
                Composer = null
            };

    private static string? MapAvailability(Availability? availability)
        => availability switch
        {
            YtDlpSharpLib.Models.Availability.Public => "public",
            YtDlpSharpLib.Models.Availability.Private => "private",
            YtDlpSharpLib.Models.Availability.PremiumOnly => "premium_only",
            YtDlpSharpLib.Models.Availability.SubscriberOnly => "subscriber_only",
            YtDlpSharpLib.Models.Availability.NeedsAuth => "needs_auth",
            YtDlpSharpLib.Models.Availability.Unlisted => "unlisted",
            _ => null
        };

    private static Instant? ResolveReleaseDate(VideoInfo info)
    {
        if (info.ReleaseTimestamp is { } releaseTimestamp)
            return Instant.FromUnixTimeSeconds(releaseTimestamp);

        if (info.Timestamp is { } timestamp)
            return Instant.FromUnixTimeSeconds(timestamp);

        return ParseDate(info.ReleaseDate) ?? (info.ParsedUploadDate is { } uploadDate
            ? Instant.FromDateTimeOffset(new DateTimeOffset(uploadDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null);
    }

    private static Instant? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd" };
        return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? Instant.FromDateTimeOffset(new DateTimeOffset(parsed.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            : null;
    }

    private static long SecondsToTicks(double? seconds)
        => seconds is null ? 0 : TimeSpan.FromSeconds(seconds.Value).Ticks;

    private static int? ToInt32(long? value)
        => value is null ? null
            : value > int.MaxValue ? int.MaxValue
            : value < int.MinValue ? int.MinValue
            : (int)value.Value;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string UnknownAccountHandle(string scope, params string?[] identityParts)
    {
        var identity = string.Join(
            ":",
            identityParts
                .Select(part => FirstNonBlank(part))
                .OfType<string>());

        return string.IsNullOrWhiteSpace(identity)
            ? "unknown"
            : $"unknown:{scope}:{identity}";
    }

    private static IReadOnlyList<string> ToList(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static IReadOnlyList<string> DistinctNonBlank(IEnumerable<string>? values)
        => values?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
}
