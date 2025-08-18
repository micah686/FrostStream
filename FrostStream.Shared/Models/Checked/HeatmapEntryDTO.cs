using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class HeatmapEntryDTO
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string VideoId { get; set; }
        public float? StartTime { get; set; }
        public float? EndTime { get; set; }
        public double? Value { get; set; }   // 0..1 normalized
    }
}
