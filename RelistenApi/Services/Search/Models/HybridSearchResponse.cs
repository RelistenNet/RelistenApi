using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Relisten.Services.Search.Models
{
    public class HybridSearchResponse
    {
        [JsonProperty("query")]
        public string Query { get; set; } = "";

        [JsonProperty("total_results")]
        public int TotalResults { get; set; }

        [JsonProperty("results")]
        public List<HybridSearchResult> Results { get; set; } = new();

        [JsonProperty("facets")]
        public SearchFacets? Facets { get; set; }
    }

    public class HybridSearchResult
    {
        [JsonProperty("show_id")]
        public long show_id { get; set; }

        [JsonProperty("source_id")]
        public long source_id { get; set; }

        [JsonProperty("artist_id")]
        public int artist_id { get; set; }

        [JsonProperty("artist_name")]
        public string artist_name { get; set; } = "";

        [JsonProperty("show_date")]
        public DateTime? show_date { get; set; }

        [JsonProperty("show_year")]
        public short? show_year { get; set; }

        [JsonProperty("venue_name")]
        public string? venue_name { get; set; }

        [JsonProperty("venue_location")]
        public string? venue_location { get; set; }

        [JsonProperty("tour_name")]
        public string? tour_name { get; set; }

        [JsonProperty("track_titles")]
        public string? track_titles { get; set; }

        [JsonProperty("is_soundboard")]
        public bool is_soundboard { get; set; }

        [JsonProperty("avg_rating")]
        public float? avg_rating { get; set; }

        [JsonProperty("num_reviews")]
        public int num_reviews { get; set; }

        [JsonProperty("taper")]
        public string? taper { get; set; }

        [JsonProperty("relevance_score")]
        public double relevance_score { get; set; }

        [JsonProperty("match_type")]
        public string match_type { get; set; } = "";
    }

    public class SearchFacets
    {
        [JsonProperty("artists")]
        public List<ArtistFacet> Artists { get; set; } = new();

        [JsonProperty("years")]
        public List<YearFacet> Years { get; set; } = new();
    }

    public class ArtistFacet
    {
        [JsonProperty("artist_id")]
        public int artist_id { get; set; }

        [JsonProperty("name")]
        public string name { get; set; } = "";

        [JsonProperty("count")]
        public int count { get; set; }
    }

    public class YearFacet
    {
        [JsonProperty("year")]
        public short year { get; set; }

        [JsonProperty("count")]
        public int count { get; set; }
    }
}
