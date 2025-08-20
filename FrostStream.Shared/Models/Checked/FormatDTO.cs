using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class FormatDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string VideoId { get; set; }      // redundant FK column (external video id)
        public Guid VideoRefId { get; set; }     // optional FK to VideoDto.Id if using GUIDs

        // core format fields
        public string FormatId { get; set; }     // format_id
        public string Url { get; set; }
        public string Ext { get; set; }
        public string Format { get; set; }       // human readable
        public string FormatNote { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public float? AspectRatio { get; set; }
        public string Resolution { get; set; }
        public string DynamicRange { get; set; } // 'SDR','HDR10',...

        // bitrates / codecs
        public float? Tbr { get; set; }          // avg bitrate kbps
        public float? Abr { get; set; }
        public float? Vbr { get; set; }
        public string ACodec { get; set; }       // acodec
        public string VCodec { get; set; }       // vcodec
        public int? Asr { get; set; }            // audio sampling rate
        public int? AudioChannels { get; set; }
        public float? Fps { get; set; }
        public string Container { get; set; }    // container
        public long? Filesize { get; set; }
        public long? FilesizeApprox { get; set; }
        public string Protocol { get; set; }

        // advanced / manifest
        public string ManifestUrl { get; set; }
        public bool? IsFromStart { get; set; }
        public int? Preference { get; set; }
        public int? Quality { get; set; }
        public int? SourcePreference { get; set; }
        public string Language { get; set; }
        public int? LanguagePreference { get; set; }
    }
}
