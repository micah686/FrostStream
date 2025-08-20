using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class ThumbnailDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string VideoId { get; set; }    // external video id
        public string ThumbId { get; set; }    // id property in thumbnails list (optional)
        public string Url { get; set; }
        public string Ext { get; set; }
        public int? Preference { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string Resolution { get; set; } // deprecated format but still present sometimes
        public long? Filesize { get; set; }
    }
}
