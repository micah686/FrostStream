using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class VideoDataDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        // External unique video id from yt-dlp (eg. YouTube id)
        [Required]
        public string VideoId { get; set; }

        // Basic metadata
        public string Title { get; set; }
        public string AltTitle { get; set; }
        public string DisplayId { get; set; }
        public string Description { get; set; }

        // Foreign Keys
        public Guid? ChannelId { get; set; } // FK to ChannelDTO
        public Guid? SeriesId { get; set; }  // FK to SeriesMetadataDTO

        // Navigation properties
        public virtual ChannelDTO Channel { get; set; }
        public virtual SeriesMetadataDTO Series { get; set; }

        // dates / timestamps (UTC)
        public DateTime? Timestamp { get; set; }
        public DateTime? UploadDate { get; set; }
        public DateTime? ReleaseTimestamp { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? ReleaseYear { get; set; }
        public DateTime? ModifiedTimestamp { get; set; }
        public DateTime? ModifiedDate { get; set; }

        // counts & stats
        public long? ViewCount { get; set; }
        public long? ConcurrentViewCount { get; set; }
        public long? LikeCount { get; set; }
        public long? DislikeCount { get; set; }
        public long? RepostCount { get; set; }
        public double? AverageRating { get; set; }
        public int? CommentCount { get; set; }

        public int? AgeLimit { get; set; }
        public TimeSpan? Duration { get; set; }
        public bool? IsLive { get; set; }
        public bool? WasLive { get; set; }
        public string LiveStatus { get; set; }
        public float? StartTime { get; set; }
        public float? EndTime { get; set; }

        // other fields
        public string License { get; set; }
        public string Location { get; set; }
        public string MediaType { get; set; }
        public string PlayableInEmbed { get; set; }
        public string Availability { get; set; }

        // summary & search optimization columns
        public string Thumbnail { get; set; }
        public string CategoriesCsv { get; set; }
        public string TagsCsv { get; set; }

        [Column(TypeName = "jsonb")]
        public string RawInfoJson { get; set; }

        // Navigation collections
        public virtual List<FormatDTO> Formats { get; set; } = new();
        public virtual List<ThumbnailDTO> Thumbnails { get; set; } = new();
        public virtual List<ChapterDTO> Chapters { get; set; } = new();
        public virtual List<SubtitleDTO> Subtitles { get; set; } = new();
        public virtual List<CommentEntityDTO> Comments { get; set; } = new();
        public virtual List<VideoTagDTO> Tags { get; set; } = new();
        public virtual List<HeatmapEntryDTO> Heatmap { get; set; } = new();
    }
}
