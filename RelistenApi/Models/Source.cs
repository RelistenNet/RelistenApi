using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public enum FlacType
    {
        NoFlac,
        Flac16Bit,
        Flac24Bit,
        NoPlayableFlac
    }

    public class SlimSource : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }

        [Required] public Guid artist_uuid { get; set; }

        [V2JsonOnly] [Required] public int? venue_id { get; set; }

        [Required] public Guid? venue_uuid { get; set; }
        public Venue venue { get; set; }

        [Required] public string display_date { get; set; }

        [Required] public bool is_soundboard { get; set; }

        [Required] public bool is_remaster { get; set; }

        [Required] public bool has_jamcharts { get; set; }

        [Required] public double avg_rating { get; set; }

        [Required] public int num_reviews { get; set; }

        public int? num_ratings { get; set; }

        [Required] public double avg_rating_weighted { get; set; }

        public double duration { get; set; }

        [Required] public string upstream_identifier { get; set; }

        [Required] public Guid uuid { get; set; }
    }

    public class SlimSourceWithShowVenueAndArtist : SlimSource
    {
        [V2JsonOnly] [Required] public int? show_id { get; set; }

        [Required] public Guid? show_uuid { get; set; }

        public Show show { get; set; }

        [Required] public SlimArtistWithFeatures artist { get; set; }
    }

    public class Source : SlimSource
    {
        [V2JsonOnly] [Required] public int? show_id { get; set; }

        [Required] public Guid show_uuid { get; set; }
        public Show show { get; set; }

        [Required] public string description { get; set; }
        [Required] public string taper_notes { get; set; }
        [Required] public string source { get; set; }
        [Required] public string taper { get; set; }
        [Required] public string transferrer { get; set; }
        [Required] public string lineage { get; set; }

        [Required]
        [JsonConverter(typeof(StringEnumConverter))]
        public FlacType flac_type { get; set; }
    }

    public class SourceReviewInformation
    {
        [V2JsonOnly] [Required] public int source_id { get; set; }

        [Required] public string upstream_identifier { get; set; }

        [Required] public int review_count { get; set; }

        [Required] public DateTime review_max_updated_at { get; set; }
    }

    public class SourceFull : Source
    {
        // public IList<SourceReview> reviews { get; set; }

        [Required] public int review_count { get; set; }

        [Required] public IList<SourceSet> sets { get; set; }

        [Required] public IList<Link> links { get; set; }
    }
}
