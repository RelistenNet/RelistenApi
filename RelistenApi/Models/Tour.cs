using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class Tour : BaseRelistenModel, IHasPersistentIdentifier
    {
        [Required]
        public int artist_id { get; set; }


        [Required]
        public DateTime? start_date { get; set; }

        [Required]
        public DateTime? end_date { get; set; }

        [Required]
        public string name { get; set; }

        [Required]
        public string slug { get; set; }

        [Required]
        public string upstream_identifier { get; set; }

        [Required]
		public Guid uuid { get; set; }
    }

    public class TourWithShowCount : Tour
    {
        [Required]
        public int shows_on_tour { get; set; }
    }

    public class TourWithShows : Tour
    {
        [Required]
        public IEnumerable<Show> shows { get; set; }
    }
}