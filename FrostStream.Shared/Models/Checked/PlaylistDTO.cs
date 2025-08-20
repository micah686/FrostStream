using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class PlaylistDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string PlaylistId { get; set; }   // external playlist id
        public string Title { get; set; }
        public int? PlaylistCount { get; set; }

        public virtual List<PlaylistEntryDTO> Entries { get; set; } = new();
    }
    public class PlaylistEntryDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string PlaylistId { get; set; }   // external playlist id (string)
        public string VideoId { get; set; }      // video id in playlist
        public string Title { get; set; }

    }
}
