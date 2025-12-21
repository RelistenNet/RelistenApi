using System;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class SetlistShow : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }

        [V2JsonOnly] [Required] public int? tour_id { get; set; }
        [Required] public Guid tour_uuid { get; set; }

        [Required] public Tour tour { get; set; } = null!;


        [Required] public int? era_id { get; set; }

        [Required] public Era era { get; set; } = null!;


        [V2JsonOnly] [Required] public int venue_id { get; set; }
        [Required] public Guid venue_uuid { get; set; }

        [Required] public Venue venue { get; set; } = null!;

        /// <summary>ONLY DATE</summary>

        [Required]
        public DateTime date { get; set; }

        [Required] public string upstream_identifier { get; set; } = null!;

        [Required] public Guid uuid { get; set; }
    }

    public class SimpleSetlistShow : BaseRelistenModel
    {
        /// <summary>ONLY DATE</summary>
        [Required]
        public DateTime date { get; set; }
    }
}
