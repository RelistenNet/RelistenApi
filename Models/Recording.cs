
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class Recording {
        public int id { get; set; }

        public DateTime createdAt { get; set; }
        public DateTime updatedAt { get; set; }

        public DateTime date { get; set; }
        public string display_date { get; set; }
        public int year { get; set; }
        public int duration { get; set; }
        public int reviews_count { get; set; }
        public double avg_rating { get; set; }
        public bool is_soundboard { get; set; }
        public string identifier { get; set; }
        public string source { get; set; }
        public string lineage { get; set; }
        public string transferer { get; set; }
        public string taper { get; set; }
        public string description { get; set; }
        public double weighted_avg { get; set; }

        [JsonIgnore]
        public string reviews { get; set; }
        public IEnumerable<RecordingReview> recordingReviews { get; set; }
        public IEnumerable<Track> tracks { get; set; }

        public Artist artist { get; set; }
        public Venue venue { get; set; }
    }

    public class RecordingReview {
        public string review { get; set; }
        public string title { get; set; }
        public string author { get; set; }
        public DateTime createdAt { get; set; }
        public int stars { get; set; }
    }

    public class SimpleRecording {
        public int id { get; set; }

        public DateTime createdAt { get; set; }
        public DateTime updatedAt { get; set; }

        public DateTime date { get; set; }
        public string display_date { get; set; }
        public int year { get; set; }
        public int duration { get; set; }
        public int reviews_count { get; set; }
        public double avg_rating { get; set; }
        public bool is_soundboard { get; set; }
        public string identifier { get; set; }
        public string source { get; set; }
        public string lineage { get; set; }
        public string transferer { get; set; }
        public string taper { get; set; }
        public double weighted_avg { get; set; }

        public Venue venue { get; set; }
        public Artist artist { get; set; }
    }
}
