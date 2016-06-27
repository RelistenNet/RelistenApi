
using System;
using System.Collections.Generic;

namespace Relisten.Api.Models
{
    public interface IModel
    {
        int id { get; set; }

        DateTime created_at { get; set; }
        DateTime updated_at { get; set; }
    }

    public class Artist : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public string upstream_identifier { get; set; }
        public string data_source { get; set; }
        public string musicbrainz_id { get; set; }
        public string name { get; set; }
        public int featured { get; set; }
        public string slug { get; set; }

        public Features features { get; set; }
    }

    public class Features
    {
        public int id { get; set; }

        public bool descriptions { get; set; }
        public bool eras { get; set; }
        public bool multiple_sources { get; set; }
        public bool reviews { get; set; }
        public bool ratings { get; set; }
        public bool tours { get; set; }
        public bool taper_notes { get; set; }
        public bool source_information { get; set; }
        public bool sets { get; set; }
        public bool per_show_venues { get; set; }
        public bool per_source_venues { get; set; }
        public bool venue_coords { get; set; }
        public bool songs { get; set; }
        public bool years { get; set; }
        public bool track_md5s { get; set; }
        public bool review_titles { get; set; }
        public bool jam_charts { get; set; }
        public bool setlist_data_incomplete { get; set; }
        public bool artist_id { get; set; }
        public bool track_names { get; set; }
    }

    public class Era : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int artist_id { get; set; }
        public Artist artist { get; set; }

        public int order { get; set; }
        public string name { get; set; }
    }

    public class SetlistShow : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int artist_id { get; set; }
        public Artist artist { get; set; }

        public int? tour_id { get; set; }
        public Tour tour { get; set; }

        public int venue_id { get; set; }
        public Venue venue { get; set; }

        /// <summary>ONLY DATE</summary>
        public DateTime date { get; set; }
        public string upstream_identifier { get; set; }
    }

    public class SetlistSong : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int artist_id { get; set; }
        public Artist artist { get; set; }

        public string name { get; set; }
        public string slug { get; set; }
        public string upstream_identifier { get; set; }

        public IEnumerable<SetlistShow> shows_played_at { get; set; }
    }

    public class Show : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int artist_id { get; set; }
        public Artist artist { get; set; }

        public int? venue_id { get; set; }
        public Venue venue { get; set; }

        public int? tour_id { get; set; }
        public Tour tour { get; set; }

        public int? year_id { get; set; }
        public Year year { get; set; }

        public int? era_id { get; set; }
        public Era era { get; set; }

        /// <summary>ONLY DATE</summary>
        public DateTime date { get; set; }

        public float avg_rating_weighted { get; set; }
        public float avg_duration { get; set; }

        public string display_date { get; set; }

        public IEnumerable<Source> sources { get; set; }
    }

    public class Source : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int show_id { get; set; }
        public Show show { get; set; }

        public bool is_soundboard { get; set; }
        public bool is_remaster { get; set; }
        public bool has_jamcharts { get; set; }

        public float avg_rating { get; set; }
        public int num_reviews { get; set; }

        public string upstream_identifier { get; set; }
        public string description { get; set; }
        public string taper_notes { get; set; }
        public string source { get; set; }
        public string taper { get; set; }
        public string transferrer { get; set; }
        public string lineage { get; set; }

        public IEnumerable<SourceReview> reviews { get; set; }
        public IEnumerable<SourceSet> sets { get; set; }
    }

    public class SourceSet : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int source_id { get; set; }
        public Source source { get; set; }

        public int index { get; set; }
        public bool is_encore { get; set; }

        public string name { get; set; }

        public IEnumerable<SourceTrack> tracks { get; set; }
    }

    public class SourceReview : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int rating { get; set; }
        public string title { get; set; }
        public string review { get; set; }
        public string author { get; set; }
    }

    public class SourceTrack : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int artist_id { get; set; }
        public int source_id { get; set; }
        public int source_set_id { get; set; }

        public int track_position { get; set; }
        public int duration { get; set; }
        public string title { get; set; }
        public string slug { get; set; }
        public string mp3_url { get; set; }
        public string md5 { get; set; }
    }

    public class Tour : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int artist_id { get; set; }
        public Artist artist { get; set; }

        public DateTime? start_date { get; set; }
        public DateTime? end_date { get; set; }
        public string name { get; set; }
        public string slug { get; set; }
        public string upstream_identifier { get; set; }
    }

    public class Venue : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int? artist_id { get; set; }

        public double? latitude { get; set; }
        public double? longitude { get; set; }
        public string name { get; set; }
        public string location { get; set; }
        public string upstream_identifier { get; set; }
    }

    public class Year : IModel
    {
        public int id { get; set; }

        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public int show_count { get; set; }
        public int recording_count { get; set; }
        public int duration { get; set; }
        public float avg_duration { get; set; }
        public float avg_rating { get; set; }

        public string year { get; set; }

        public int artist_id { get; set; }
        public Artist artist { get; set; }
    }
}
