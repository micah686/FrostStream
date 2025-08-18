using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked.LiveChat
{
    /*
      ChatAuthorDto             EmoteDto
      ▲                       ▲
      │                       │
      │                       │
LiveChatMessageEventDto   ChatMessageTokenDto
      ▲                       ▲
      │                       │
      └──────────┐     ┌──────┘
                 ▼     ▼
         LiveChatMessageTextDto


    ChatAuthorDto — Stores unique chat participants across all videos.

EmoteDto — Stores unique custom emotes (image + metadata) once.

LiveChatMessageTextDto — Stores the unique text of a chat message once, exactly as received.

ChatMessageTokenDto — Breaks a message into ordered parts (text + emotes).

LiveChatMessageEventDto — Represents an instance of a message appearing in chat, linking:

    AuthorId → who sent it

    MessageTextId → which unique message string it was

    VideoId → which live chat it belonged to

    TimestampUtc → when it appeared

    SuperChat fields → if applicable
     */


    public class LiveChatMessageTextDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string RawText { get; set; }              // Original text exactly as received

        public string NormalizedText { get; set; }       // Lowercase/trimmed for deduplication
        public string Sha1Hash { get; set; }             // For fast lookup

        // Navigation to tokenized form (for rendering with emotes)
        public virtual List<ChatMessageTokenDTO> Tokens { get; set; } = new();
    }
    public enum ChatMessageTokenType
    {
        Text,   // Plain text (Unicode emoji included)
        Emote   // Reference to a custom emote
    }
    public class ChatAuthorDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // External identifiers
        [Required]
        public string ExternalAuthorId { get; set; }   // YouTube channel/user ID

        public string DisplayName { get; set; }
        public string ProfileUrl { get; set; }
        public string AvatarUrl { get; set; }

        // Optional: last time we saw this author in a chat
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        // Optional: raw JSON for flexibility
        [Column(TypeName = "jsonb")]
        public string RawInfoJson { get; set; }
    }
    public class EmoteDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // Platform-specific emote identifiers
        [Required]
        public string Name { get; set; }                 // Normalized name (e.g., "pogg")
        public string ExternalId { get; set; }           // Optional numeric/string id from platform

        // Ownership (for channel-specific emotes)
        public string OwnerChannelId { get; set; }

        // Image info
        public string ImageUrl { get; set; }
        public string LocalPath { get; set; }            // Cached image location on disk
        public int? Width { get; set; }
        public int? Height { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [Column(TypeName = "jsonb")]
        public string RawInfoJson { get; set; }
    }
    

    public class ChatMessageTokenDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid MessageTextId { get; set; }          // FK to LiveChatMessageTextDto
        public virtual LiveChatMessageTextDTO MessageText { get; set; }

        public int SequenceIndex { get; set; }           // Position in message

        public ChatMessageTokenType TokenType { get; set; }

        // Text token fields
        public string TextContent { get; set; }

        // Emote token fields
        public Guid? EmoteId { get; set; }
        public virtual EmoteDTO Emote { get; set; }

        public string RawToken { get; set; }             // Original matched text
    }
    public class LiveChatMessageEventDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string VideoId { get; set; }              // FK to VideoDto.VideoId

        public Guid AuthorId { get; set; }               // FK to ChatAuthorDto
        public virtual ChatAuthorDTO Author { get; set; }

        public Guid MessageTextId { get; set; }          // FK to LiveChatMessageTextDto
        public virtual LiveChatMessageTextDTO MessageText { get; set; }

        public DateTime TimestampUtc { get; set; }

        // SuperChat info
        public bool? IsSuperChat { get; set; }
        public decimal? SuperChatAmount { get; set; }
        public string SuperChatCurrency { get; set; }
    }


}
