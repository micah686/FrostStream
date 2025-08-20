using System;
using System.Collections.Generic;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Riok.Mapperly.Abstractions;
using YoutubeDLSharp.Metadata;
using FrostStream.Shared.Models.Checked;

namespace FrostStream.Worker.Utilities
{
    [Mapper]
    public partial class MetadataHelper
    {

        // Main mapping for VideoData to VideoDataDTO (assuming single video; handle playlists separately)
        public VideoDataDTO MapToVideo(VideoData source)
        {
            if (source.ResultType == MetadataType.Playlist || source.ResultType == MetadataType.MultiVideo)
            {
                throw new InvalidOperationException("Use MapToPlaylist for playlist types.");
            }

            var target = new VideoDataDTO
            {
                Id = Guid.NewGuid(),
                VideoId = source.ID,
                Provider = source.ExtractorKey ?? source.Extractor ?? "Unknown",
                Title = source.Title,
                AltTitle = source.AltTitle,
                DisplayId = source.DisplayID,
                Description = source.Description,
                Timestamp = source.Timestamp,
                UploadDate = source.UploadDate,
                ReleaseTimestamp = source.ReleaseTimestamp,
                ReleaseDate = source.ReleaseDate,
                ReleaseYear = !string.IsNullOrEmpty(source.ReleaseYear) ? int.Parse(source.ReleaseYear) : null,
                ModifiedTimestamp = source.ModifiedTimestamp,
                ModifiedDate = source.ModifiedDate,
                ViewCount = source.ViewCount,
                ConcurrentViewCount = source.ConcurrentViewCount,
                LikeCount = source.LikeCount,
                DislikeCount = source.DislikeCount,
                RepostCount = source.RepostCount,
                AverageRating = source.AverageRating,
                CommentCount = source.CommentCount,
                AgeLimit = source.AgeLimit,
                Duration = source.Duration.HasValue ? TimeSpan.FromSeconds(source.Duration.Value) : null,
                IsLive = source.IsLive,
                WasLive = source.WasLive,
                LiveStatus = source.LiveStatus.ToString(),
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                License = source.License,
                Location = source.Location,
                // MediaType not directly mapped
                PlayableInEmbed = source.PlayableInEmbed,
                Availability = source.Availability?.ToString() ?? "Unknown",
                Thumbnail = source.Thumbnail,
                CategoriesCsv = source.Categories.Length >0 ? string.Join(",", source.Categories) : "",
                TagsCsv = source.Tags.Length >0 ? string.Join(",", source.Tags) : ""
            };

            // Map channel
            target.Channel = MapToChannel(source);
            target.ChannelId = target.Channel?.Id;

            // Map series metadata
            target.Series = MapToSeriesMetadata(source);
            target.SeriesId = target.Series?.Id;

            // Map collections
            target.Formats = source.Formats?.Select(MapToFormat).ToList() ?? new List<FormatDTO>();
            foreach (var fmt in target.Formats)
            {
                fmt.VideoId = source.ID;
            }

            target.Thumbnails = source.Thumbnails?.Select(t => MapToThumbnail(t, source.ID)).ToList() ?? new List<ThumbnailDTO>();

            target.Chapters = MapChapters(source.Chapters, source.ID);

            target.Subtitles = MapSubtitles(source.Subtitles, source.ID, false)
                .Concat(MapSubtitles(source.AutomaticCaptions, source.ID, true))
                .ToList();

            target.Comments = source.Comments?.Select(c => MapToComment(c, source.ID)).ToList() ?? new List<CommentEntityDTO>();

            target.Tags = source.Tags?.Select(t => new VideoTagDTO { Id = Guid.NewGuid(), VideoId = source.ID, Tag = t }).ToList() ?? new List<VideoTagDTO>();
            
            // Heatmap not available in source
            target.Heatmap = new List<HeatmapEntryDTO>();

            return target;
        }

        // Mapping for playlist
        public PlaylistDTO MapToPlaylist(VideoData source)
        {
            if (source.ResultType != MetadataType.Playlist && source.ResultType != MetadataType.MultiVideo)
            {
                throw new InvalidOperationException("Source is not a playlist type.");
            }

            var target = new PlaylistDTO
            {
                Id = Guid.NewGuid(),
                PlaylistId = source.ID,
                Title = source.Title,
                PlaylistCount = source.Entries?.Length
            };

            target.Entries = source.Entries?.Select(e => new PlaylistEntryDTO
            {
                Id = Guid.NewGuid(),
                PlaylistId = source.ID,
                VideoId = e.ID,
                Title = e.Title
            }).ToList() ?? new List<PlaylistEntryDTO>();

            return target;
        }

        //mappings for categories
        public List<VideoCategoryDTO> MapCategories(VideoData source)
        {
            return source.Categories?.Select(c => new VideoCategoryDTO
            {
                Id = Guid.NewGuid(),
                VideoId = source.ID,
                Category = c
            }).ToList() ?? new List<VideoCategoryDTO>();
        }

        // mappings for Contributors (cast, artist, composer, creator)
        public List<VideoContributorDTO> MapContributors(VideoData source)
        {
            var list = new List<VideoContributorDTO>();
            if (source.Cast != null)
            {
                list.AddRange(source.Cast.Select(c =>
                    new VideoContributorDTO { Id = Guid.NewGuid(), VideoId = source.ID, Role = "cast", Name = c }));
            }
            if (!string.IsNullOrEmpty(source.Artist))
            {
                list.Add(new VideoContributorDTO { Id = Guid.NewGuid(), VideoId = source.ID, Role = "artist", Name = source.Artist });
            }
            if (!string.IsNullOrEmpty(source.Composer))
            {
                list.Add(new VideoContributorDTO { Id = Guid.NewGuid(), VideoId = source.ID, Role = "composer", Name = source.Composer });
            }
            if (!string.IsNullOrEmpty(source.Creator) && source.Creator != source.Uploader)
            {
                list.Add(new VideoContributorDTO { Id = Guid.NewGuid(), VideoId = source.ID, Role = "creator", Name = source.Creator });
            }
            return list;
        }

        // Generated mappings for sub-objects
        public partial FormatDTO MapToFormat(FormatData source);

        private ThumbnailDTO MapToThumbnail(ThumbnailData source, string videoId)
        {
            var target = new ThumbnailDTO
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                ThumbId = source.ID,
                Url = source.Url,
                Preference = source.Preference,
                Width = source.Width,
                Height = source.Height,
                Resolution = source.Resolution,
                Filesize = source.Filesize
            };
            return target;
        }

        private List<ChapterDTO> MapChapters(ChapterData[] chapters, string videoId)
        {
            if (chapters == null) return new List<ChapterDTO>();

            return chapters.Select(c =>
            {
                return new ChapterDTO
                {
                    Id = Guid.NewGuid(),
                    VideoId = videoId,
                    // No language/ext/url/data for chapters; store in RawJson
                    LanguageTag = null,
                    Ext = "json",
                    Url = null,
                    Data = null,
                    Name = c.Title
                };
            }).ToList();
        }

        private List<SubtitleDTO> MapSubtitles(Dictionary<string, SubtitleData[]> subtitles, string videoId, bool isAuto)
        {
            var list = new List<SubtitleDTO>();
            if (subtitles == null) return list;

            foreach (var kvp in subtitles)
            {
                string lang = kvp.Key;
                foreach (var sub in kvp.Value)
                {
                    list.Add(new SubtitleDTO
                    {
                        Id = Guid.NewGuid(),
                        VideoId = videoId,
                        LanguageTag = isAuto ? $"{lang}-auto" : lang,
                        Ext = sub.Ext,
                        Url = sub.Url,
                        Data = sub.Data,
                        Name = sub.Name
                    });
                }
            }
            return list;
        }

        private CommentEntityDTO MapToComment(CommentData source, string videoId)
        {
            return new CommentEntityDTO
            {
                Id = Guid.NewGuid(),
                VideoId = videoId,
                CommentId = source.ID,
                Author = source.Author,
                AuthorId = source.AuthorID,
                AuthorThumbnail = source.AuthorThumbnail,
                AuthorIsUploader = source.AuthorIsUploader,
                Html = source.Html,
                Text = source.Text,
                Timestamp = source.Timestamp == DateTime.MinValue ? (DateTime?)null : source.Timestamp,
                ParentId = source.Parent,
                LikeCount = source.LikeCount,
                DislikeCount = source.DislikeCount,
                IsFavorited = source.IsFavorited
            };
        }

        private ChannelDTO MapToChannel(VideoData source)
        {
            if (string.IsNullOrEmpty(source.ChannelID) && string.IsNullOrEmpty(source.UploaderID)) return null;

            return new ChannelDTO
            {
                Id = Guid.NewGuid(),
                ChannelId = source.ChannelID ?? source.UploaderID,
                ChannelName = source.Channel ?? source.Uploader,
                ChannelUrl = source.ChannelUrl ?? source.UploaderUrl,
                UploaderName = source.Uploader,
                UploaderId = source.UploaderID,
                UploaderUrl = source.UploaderUrl,
                // Description not in source
                FollowerCount = source.ChannelFollowerCount,
                // IsVerified not in source
                // Country not in source
                // AvatarUrl not in source
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        private SeriesMetadataDTO MapToSeriesMetadata(VideoData source)
        {
            if (string.IsNullOrEmpty(source.Series) && string.IsNullOrEmpty(source.Season) && string.IsNullOrEmpty(source.Episode)) return null;

            return new SeriesMetadataDTO
            {
                Id = Guid.NewGuid(),
                SeriesTitle = source.Series,
                SeriesId = source.SeriesId,
                SeasonTitle = source.Season,
                SeasonNumber = source.SeasonNumber,
                SeasonId = source.SeasonId,
                EpisodeTitle = source.Episode,
                EpisodeNumber = source.EpisodeNumber,
                EpisodeId = source.EpisodeId
            };
        }

        // Add other partial mappings if needed


    }
}
