using System;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class SourceTrack : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int source_id { get; set; }
        [Required] public Guid source_uuid { get; set; }

        [V2JsonOnly] [Required] public int source_set_id { get; set; }
        [Required] public Guid source_set_uuid { get; set; }

        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }

        [Required] public Guid show_uuid { get; set; }

        [Required] public int track_position { get; set; }

        public int? duration { get; set; }

        [Required] public string title { get; set; } = null!;

        [Required] public string slug { get; set; } = null!;

        public string? mp3_url { get; set; }
        public string? mp3_md5 { get; set; }

        public string? flac_url { get; set; }
        public string? flac_md5 { get; set; }

        [Required] public Guid uuid { get; set; }

        /// <summary>What this track is: song, banter, tuning, crowd, etc.</summary>
        [Required] public string track_type { get; set; } = "song";

        /// <summary>Best-match canonical song ID, if matched.</summary>
        public int? matched_song_id { get; set; }

        /// <summary>Confidence of the best match (0.0 to 1.0).</summary>
        public float? match_confidence { get; set; }

        /// <summary>How the match was made: "slug", "fuzzy", "llm".</summary>
        public string? match_method { get; set; }
    }

    public class PlayedSourceTrack
    {
        [Required] public SlimSourceWithShowVenueAndArtist source { get; set; } = null!;

        [Required] public SourceTrack track { get; set; } = null!;
    }

    public class LivePlayedTrack
    {
        [Required] public DateTime played_at { get; set; }

        [Required] public int track_id { get; set; }

        [Required] public Guid uuid { get; set; } = Guid.Empty;

        [Required] public string app_type { get; set; } = null!;

        public PlayedSourceTrack? track { get; set; }
    }
}
