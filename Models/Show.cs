
using System;
using System.Collections.Generic;

namespace Relisten.Api.Models
{
    public class SimpleShow {
        public DateTime date { get; set; }
        public string display_date { get; set; }
        public int year { get; set; }
        public Artist artist { get; set; }
        public double avg_rating { get; set; }
        public int review_count { get; set; }
        public bool has_soundboard { get; set; }
        public int avg_duration { get; set; }

        public IEnumerable<SimpleRecording> recordings { get; set; }        
    }

    public class Show {
        public DateTime date { get; set; }
        public string display_date { get; set; }
        public int year { get; set; }
        public Artist artist { get; set; }
        public double avg_rating { get; set; }
        public int review_count { get; set; }
        public bool has_soundboard { get; set; }
        public int avg_duration { get; set; }

        public IEnumerable<Recording> recordings { get; set; }        
    }
}
