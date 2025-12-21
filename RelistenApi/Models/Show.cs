using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class Show : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }
        [V2JsonOnly] public int? venue_id { get; set; }
        public VenueWithShowCount venue { get; set; } = null!;
        public Guid? venue_uuid { get; set; }
        [V2JsonOnly] public int? tour_id { get; set; }
        public Guid? tour_uuid { get; set; }
        public Tour tour { get; set; } = null!;
        [V2JsonOnly] public int year_id { get; set; }
        public Guid year_uuid { get; set; }
        public Year year { get; set; } = null!;
        [V2JsonOnly] public int? era_id { get; set; }
        public Era era { get; set; } = null!;

        /// <summary>ONLY DATE</summary>
        [Required]
        public DateTime date { get; set; }

        [Required] public float avg_rating { get; set; }
        public float? avg_duration { get; set; }
        [Required] public string display_date { get; set; } = null!;

        /// <summary>
        ///     This can be used to determine the age of the source most recently added. `updated_at`
        ///     for a source comes from the upstream source and won't be suffer recently imported biases.
        ///     The value pulled from upstream is based on when that source was published/updated, not
        ///     counting reviews, ratings, etc.
        /// </summary>
        /// <value>The most recent `updated_at` date of all the sources for this show.</value>
        [Required]
        public DateTime most_recent_source_updated_at { get; set; }

        /// <value><c>true</c> if this show has at least one source that is a soundboard recording; otherwise, <c>false</c>.</value>
        [Required]
        public bool has_soundboard_source { get; set; }

        [Required] public bool has_streamable_flac_source { get; set; }
        [Required] public int source_count { get; set; }
        [Required] public Guid uuid { get; set; }
    }

    public class ShowWithArtist : Show
    {
        [Required] public Artist artist { get; set; } = null!;
    }

    public class ShowWithSources : Show
    {
        [Required] public IEnumerable<SourceFull> sources { get; set; } = null!;
    }
}
