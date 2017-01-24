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
        public int artist_id { get; set; }

        public int? show_id { get; set; }
        public Show show { get; set; }

        public int? venue_id { get; set; }
        public Venue venue { get; set; }

        public string display_date { get; set; }

        public bool is_soundboard { get; set; }
        public bool is_remaster { get; set; }
        public bool has_jamcharts { get; set; }

        public double avg_rating { get; set; }
        public int num_reviews { get; set; }
        public int? num_ratings { get; set; }
        public double avg_rating_weighted { get; set; }
        public double duration { get; set; }

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
        public IList<SourceReview> reviews { get; set; }
        public IList<SourceSet> sets { get; set; }
    }
}