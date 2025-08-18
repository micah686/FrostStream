using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class ChannelDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // External IDs
        [Required]
        public string ChannelId { get; set; }      // YouTube or platform channel ID

        public string ChannelName { get; set; }
        public string ChannelUrl { get; set; }
        public string UploaderName { get; set; }   // Sometimes different from channel name
        public string UploaderId { get; set; }     // Platform uploader ID
        public string UploaderUrl { get; set; }
        public string Description { get; set; }

        // Counts (these can change, so keep in this table, not VideoDto)
        public long? FollowerCount { get; set; }
        public bool? IsVerified { get; set; }

        // Metadata
        public string Country { get; set; }
        public string AvatarUrl { get; set; }

        // Timestamps for when we last synced this channel info
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

        // Optional: store raw JSON from extractor
        [Column(TypeName = "jsonb")]
        public string RawInfoJson { get; set; }
    }

}
