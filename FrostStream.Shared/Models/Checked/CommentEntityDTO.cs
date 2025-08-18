using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    // -----------------------
    // Comments - normalized entity (based on your CommentData.cs)
    // -----------------------
    public class CommentEntityDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string VideoId { get; set; }        // external video id this comment belongs to
        public string CommentId { get; set; }      // "id" from yt-dlp
        public string Author { get; set; }         // author
        public string AuthorId { get; set; }       // author_id
        public string AuthorThumbnail { get; set; }// author_thumbnail
        public string AuthorUrl { get; set; }      // optional author_url (not in your file but present in spec)
        public bool? AuthorIsUploader { get; set; }
        public bool? AuthorIsVerified { get; set; }
        public string Html { get; set; }
        public string Text { get; set; }
        public DateTime? Timestamp { get; set; }   // convert UNIX -> UTC
        public string ParentId { get; set; }       // parent (root or comment id)
        public int? LikeCount { get; set; }
        public int? DislikeCount { get; set; }
        public bool? IsFavorited { get; set; }

        [Column(TypeName = "jsonb")]
        public string RawJson { get; set; }        // original raw comment dict (optional)
    }
}
