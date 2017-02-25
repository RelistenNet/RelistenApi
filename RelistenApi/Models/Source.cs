using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class Source : BaseRelistenModel
    {
		[Required]
        public int artist_id { get; set; }

        public int? show_id { get; set; }
        public Show show { get; set; }

        public int? venue_id { get; set; }
        public Venue venue { get; set; }

		[Required]
        public string display_date { get; set; }

  		[Required]
  	    public bool is_soundboard { get; set; }

		[Required]
		public bool is_remaster { get; set; }

		[Required]
        public bool has_jamcharts { get; set; }

		[Required]
        public double avg_rating { get; set; }

		[Required]
        public int num_reviews { get; set; }

		public int? num_ratings { get; set; }
        
		[Required]
		public double avg_rating_weighted { get; set; }

		public double duration { get; set; }

		[Required]
        public string upstream_identifier { get; set; }
  
		public string description { get; set; }
        public string taper_notes { get; set; }
        public string source { get; set; }
        public string taper { get; set; }
        public string transferrer { get; set; }
        public string lineage { get; set; }
    }

    public class SourceFull : Source
    {
		[Required]
        public IList<SourceReview> reviews { get; set; }
  
		[Required]
		public IList<SourceSet> sets { get; set; }
    }
}