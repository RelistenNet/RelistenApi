using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class Year : BaseRelistenModel, IHasPersistentIdentifier
    {
        [Required] public int show_count { get; set; }
        [Required] public int source_count { get; set; }
        public int? duration { get; set; }
        public float? avg_duration { get; set; }
        [Required] public float avg_rating { get; set; }
        [Required] public string year { get; set; } = null!;
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }
        [Required] public Guid uuid { get; set; }
        [V3JsonOnly] public PopularityMetrics? popularity { get; set; }
    }

    public class YearWithShows : Year
    {
        [Required] public List<Show> shows { get; set; } = null!;
    }
}
