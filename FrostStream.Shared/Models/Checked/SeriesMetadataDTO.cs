using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FrostStream.Shared.Models.Checked
{
    public class SeriesMetadataDTO
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string SeriesTitle { get; set; }
        public string SeriesId { get; set; }

        public string SeasonTitle { get; set; }
        public int? SeasonNumber { get; set; }
        public string SeasonId { get; set; }

        public string EpisodeTitle { get; set; }
        public int? EpisodeNumber { get; set; }
        public string EpisodeId { get; set; }
    }

}
