using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class VideoCategoryDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string VideoId { get; set; }
        public string Category { get; set; }
    }
}
