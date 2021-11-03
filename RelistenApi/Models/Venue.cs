using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class Venue : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }

        public double? latitude { get; set; }
        public double? longitude { get; set; }

        [Required] public string name { get; set; }
        [Required] public string location { get; set; }
        [Required] public string upstream_identifier { get; set; }
        [Required] public string slug { get; set; }
        public string past_names { get; set; }

        [Required]
        public string sortName
        {
            get
            {
                if (name.StartsWith("The ", StringComparison.CurrentCultureIgnoreCase))
                {
                    return name.Substring(4) + ", The";
                }

                return name;
            }
        }

        [Required] public Guid uuid { get; set; }
    }

    public class VenueWithShowCount : Venue
    {
        [Required] public int shows_at_venue { get; set; }
    }

    public class VenueWithShows : VenueWithShowCount
    {
        [Required] public List<Show> shows { get; set; }
    }
}
