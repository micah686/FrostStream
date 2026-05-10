using System.Globalization;
using NodaTime;
using Shared.Metadata;
using YtDlpSharpLib.Models;

namespace Worker.Metadata;

internal static class YtDlpMetadataMapper
{
    public static CapturedMediaMetadata Map(VideoInfo info, string platform, IClock clock)
    {
        var scrapedAt = clock.GetCurrentInstant();

        var account = new CapturedAccountMetadata
        {
            Platform = platform,
            AccountName = FirstNonBlank(info.Uploader, info.Channel, info.Creator) ?? "unknown",
            AccountHandle = FirstNonBlank(info.UploaderId, info.ChannelId, info.Uploader, info.Channel) ?? "unknown",
            AccountUrl = FirstNonBlank(info.UploaderUrl, info.ChannelUrl),
            FollowerCount = info.ChannelFollowerCount,
            Description = null
        };

        return new CapturedMediaMetadata
        {
            Account = account,
            Media = new CapturedMediaMetadataCore
            {
                ExternalMediaId = FirstNonBlank(info.Id, info.DisplayId),
                MetadataScrapeDate = scrapedAt,
                ThumbnailStoragePath = info.Thumbnails?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Url))?.Url,
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
            Captions = BuildCaptions(info),
            Comments = BuildComments(info, platform, scrapedAt),
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

        return new CapturedMediaTechnicalMetadata
        {
            DurationTicks = durationTicks,
            Format = new CapturedFormatMetadata
            {
                DurationTicks = durationTicks,
                StartTimeTicks = 0,
                FormatLongNames = FirstNonBlank(info.Format, info.Extension) ?? "",
                StreamCount = info.Formats?.Count ?? 0,
                BitRate = 0
            },
            Streams = [],
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

    private static IReadOnlyList<CapturedCaptionMetadata> BuildCaptions(VideoInfo info)
    {
        var captions = new List<CapturedCaptionMetadata>();
        AddCaptions(captions, info.Subtitles, "subtitles");
        AddCaptions(captions, info.AutomaticCaptions, "automatic_captions");
        return captions;
    }

    private static void AddCaptions(
        List<CapturedCaptionMetadata> captions,
        IReadOnlyDictionary<string, IReadOnlyList<SubtitleTrack>>? tracks,
        string captionType)
    {
        if (tracks is null)
            return;

        foreach (var (language, variants) in tracks)
        {
            foreach (var track in variants)
            {
                var storagePath = FirstNonBlank(track.Url, track.Data);
                if (storagePath is null)
                    continue;

                captions.Add(new CapturedCaptionMetadata
                {
                    StoragePath = storagePath,
                    CaptionType = captionType,
                    LanguageCode = string.IsNullOrWhiteSpace(language) ? "und" : language,
                    Name = FirstNonBlank(track.Name, track.Ext)
                });
            }
        }
    }

    private static IReadOnlyList<CapturedCommentMetadata> BuildComments(VideoInfo info, string platform, Instant fallbackTime)
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
                    AccountHandle = FirstNonBlank(c.AuthorId, c.Author) ?? "unknown",
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

    private static IReadOnlyList<string> ToList(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static IReadOnlyList<string> DistinctNonBlank(IEnumerable<string>? values)
        => values?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
}
