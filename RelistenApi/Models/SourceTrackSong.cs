using System;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
    /// <summary>
    /// Junction table linking a SourceTrack to a SetlistSong.
    /// Supports many-to-many: a medley track can match multiple songs,
    /// and a single song can be performed across many tracks/sources.
    /// </summary>
    public class SourceTrackSong
    {
        [Required] public int id { get; set; }
        [Required] public int source_track_id { get; set; }
        [Required] public int setlist_song_id { get; set; }

        /// <summary>Confidence of the match (0.0 to 1.0).</summary>
        [Required] public float confidence { get; set; }

        /// <summary>How the match was made: "slug", "fuzzy", "embedding", "llm".</summary>
        [Required] public string method { get; set; } = "slug";

        /// <summary>Position of this song within a multi-song track (0-indexed).</summary>
        [Required] public short position { get; set; }

        [Required] public DateTime created_at { get; set; }
    }
}
