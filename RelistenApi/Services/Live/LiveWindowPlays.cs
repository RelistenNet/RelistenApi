using System;
using System.Collections.Generic;
using Relisten.Api.Models;

namespace Relisten.Services.Live
{
    /// <summary>
    /// Plays in one live window, already grouped by Postgres. Only plays whose track,
    /// source and show still exist are counted.
    /// </summary>
    public sealed class LiveWindowPlays
    {
        public IReadOnlyList<LiveAppTypeRow> AppTypes { get; init; } = new List<LiveAppTypeRow>();
        public IReadOnlyList<LiveTimelineRow> Timeline { get; init; } = new List<LiveTimelineRow>();

        /// <summary>Every show played in the window, unranked.</summary>
        public IReadOnlyList<LiveShowRow> Shows { get; init; } = new List<LiveShowRow>();

        /// <summary>Every artist played in the window, with its same-hour-of-week baseline.</summary>
        public IReadOnlyList<LiveArtistRow> Artists { get; init; } = new List<LiveArtistRow>();
    }

    public sealed class LiveAppTypeRow
    {
        public SourceTrackPlayAppType app_type { get; set; }
        public long plays { get; set; }
    }

    public sealed class LiveTimelineRow
    {
        public DateTime bucket_start { get; set; }
        public long plays { get; set; }
    }

    public sealed class LiveShowRow
    {
        public long show_id { get; set; }
        public Guid show_uuid { get; set; }
        public int artist_id { get; set; }
        public string display_date { get; set; } = string.Empty;
        public long plays { get; set; }
        public int source_count { get; set; }
        public DateTime first_played_at { get; set; }
        public DateTime last_played_at { get; set; }
        public long last_play_id { get; set; }
        public long plays_30d { get; set; }
        public long source_id { get; set; }
        public DateTime source_created_at { get; set; }
        public Guid track_uuid { get; set; }
        public string track_title { get; set; } = string.Empty;
        public string track_slug { get; set; } = string.Empty;
        public int track_position { get; set; }
        public int? track_duration { get; set; }
        public DateTime track_played_at { get; set; }
        public int[] timeline { get; set; } = Array.Empty<int>();
    }

    public sealed class LiveArtistRow
    {
        public int artist_id { get; set; }
        public Guid artist_uuid { get; set; }
        public string artist_name { get; set; } = string.Empty;
        public string artist_slug { get; set; } = string.Empty;
        public long plays { get; set; }
        public int show_count { get; set; }
        public double expected_plays { get; set; }
    }
}
