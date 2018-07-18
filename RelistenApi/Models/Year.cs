using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class Year : BaseRelistenModel, IHasPersistentIdentifier
    {
        [Required]
        public int show_count { get; set; }

        [Required]
        public int source_count { get; set; }

        public int? duration { get; set; }
        public float? avg_duration { get; set; }

        [Required]
        public float avg_rating { get; set; }


        [Required]
        public string year { get; set; }


        [Required]
        public int artist_id { get; set; }

		[Required]
		public Guid uuid { get; set; }
    }

    public class YearWithShows : Year
    {

        [Required]
        public List<Show> shows { get; set; }
    }
}