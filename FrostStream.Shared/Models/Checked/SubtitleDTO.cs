using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class SubtitleDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string VideoId { get; set; }
        public string LanguageTag { get; set; }    // e.g. "en", the key in subtitles dictionary
        public string Ext { get; set; }            // ext
        public string Url { get; set; }            // url (if remote)
        public string Data { get; set; }           // data (base64 or raw) if the extractor provided contents
        public string Name { get; set; }           // name
        [Column(TypeName = "jsonb")]
        public string RawJson { get; set; }        // original subformat descriptor
    }
}
