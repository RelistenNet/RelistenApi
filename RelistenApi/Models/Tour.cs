using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class Tour : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }
        [Required] public DateTime? start_date { get; set; }
        [Required] public DateTime? end_date { get; set; }
        [Required] public string name { get; set; } = null!;
        [Required] public string slug { get; set; } = null!;
        [Required] public string upstream_identifier { get; set; } = null!;
        [Required] public Guid uuid { get; set; }
    }

    public class TourWithShowCount : Tour
    {
        [Required] public int shows_on_tour { get; set; }
    }

    public class TourWithShows : Tour
    {
        [Required] public IEnumerable<Show> shows { get; set; } = null!;
    }
}
