using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models.Api
{
    /// <summary>
    /// What is being played right now, grouped by show, for the window ending at
    /// <see cref="window_end"/>. Every count is plays (track starts), not listeners:
    /// plays rarely carry a user id, so listeners cannot be counted. Like the rest of
    /// v3, catalog objects are identified by uuid only.
    /// </summary>
    public class LiveSummary
    {
        public DateTime generated_at { get; set; }
        public int window_minutes { get; set; }
        public DateTime window_start { get; set; }

        /// <summary>Exclusive end of the window, aligned to the summary's refresh interval.</summary>
        public DateTime window_end { get; set; }

        /// <summary>Largest source_track_plays.id in the window; null when it is empty.</summary>
        public long? last_play_id { get; set; }
        public long total_plays { get; set; }

        /// <summary>
        /// Sum of the played tracks' durations, counting 420 s for a track with no
        /// duration. listened_seconds / 60 / window_minutes estimates how many people are
        /// listening (Little's law); it reads a little high, because a skipped track
        /// counts as if it played through.
        /// </summary>
        public long listened_seconds { get; set; }
        public IReadOnlyList<LiveSummaryAppTypePlays> app_types { get; set; } =
            new List<LiveSummaryAppTypePlays>();

        /// <summary>One bucket per minute, oldest first, zero-filled to window_minutes buckets.</summary>
        public IReadOnlyList<LiveSummaryTimelineBucket> timeline { get; set; } =
            new List<LiveSummaryTimelineBucket>();

        public int artist_count { get; set; }

        /// <summary>Plays from artists not included in <see cref="artists"/>.</summary>
        public long other_artists_plays { get; set; }
        public IReadOnlyList<LiveSummaryShow> shows { get; set; } = new List<LiveSummaryShow>();
        public IReadOnlyList<LiveSummaryArtist> artists { get; set; } =
            new List<LiveSummaryArtist>();
        public IReadOnlyList<LiveSummaryShow> deep_cuts { get; set; } = new List<LiveSummaryShow>();
        public IReadOnlyList<LiveSummaryFreshShow> fresh { get; set; } =
            new List<LiveSummaryFreshShow>();
        public LiveSummaryOnThisDay on_this_day { get; set; } = new();
    }

    public class LiveSummaryAppTypePlays
    {
        public SourceTrackPlayAppType app_type { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public SourceTrackPlayAppType app_type_description => app_type;
        public long plays { get; set; }
    }

    public class LiveSummaryTimelineBucket
    {
        public DateTime bucket_start { get; set; }
        public long plays { get; set; }
    }

    public class LiveSummaryShow
    {
        public Guid show_uuid { get; set; }
        public long plays { get; set; }

        /// <summary>See <see cref="LiveSummary.listened_seconds"/>.</summary>
        public long listened_seconds { get; set; }

        /// <summary>
        /// Plays in the 30 days before the window, from the continuous aggregates, which
        /// are materialized up to a few hours before the window.
        /// </summary>
        public long plays_30d { get; set; }

        /// <summary>The show's average 30-day rate scaled to the window length.</summary>
        public double expected_plays { get; set; }

        /// <summary>
        /// (plays + K) / (expected_plays + K), reduced for anniversaries; see
        /// LiveSummaryRanking.
        /// </summary>
        public double surprise { get; set; }

        /// <summary>
        /// The show is from a past year and its date falls on month_day. Its surprise is
        /// multiplied by LiveSummaryRanking.AnniversarySurpriseMultiplier.
        /// </summary>
        public bool is_anniversary { get; set; }

        /// <summary>
        /// When the representative source's row was created. Re-ingestion can recreate
        /// rows, so this is not a reliable "first imported" date.
        /// </summary>
        public DateTime source_created_at { get; set; }

        /// <summary>Distinct sources of this show played in the window.</summary>
        public int source_count { get; set; }
        public DateTime first_played_at { get; set; }
        public DateTime last_played_at { get; set; }

        /// <summary>
        /// The source played most in the window, ties going to the most recently played;
        /// the same shape live/history embeds as track.source.
        /// </summary>
        public SlimSourceWithShowVenueAndArtist source { get; set; } = null!;
        public LiveSummaryTrack latest_track { get; set; } = new();

        /// <summary>Per-minute plays of this show, aligned with the top-level timeline.</summary>
        public IReadOnlyList<int> timeline { get; set; } = new List<int>();
    }

    public class LiveSummaryFreshShow : LiveSummaryShow
    {
        /// <summary>
        /// Why the show is fresh. Only "recent_show" (a concert from the last fresh_days
        /// days) is produced today; "new_tape" is reserved until sources record when they
        /// were first published upstream.
        /// </summary>
        public string reason { get; set; } = LiveSummaryFreshReason.RecentShow;
    }

    public static class LiveSummaryFreshReason
    {
        public const string RecentShow = "recent_show";
    }

    public class LiveSummaryTrack
    {
        public Guid uuid { get; set; }
        public string title { get; set; } = string.Empty;
        public string slug { get; set; } = string.Empty;
        public int track_position { get; set; }
        public int? duration { get; set; }
        public DateTime played_at { get; set; }
    }

    public class LiveSummaryArtist
    {
        public Guid artist_uuid { get; set; }
        public string name { get; set; } = string.Empty;
        public string slug { get; set; } = string.Empty;
        public long plays { get; set; }

        /// <summary>See <see cref="LiveSummary.listened_seconds"/>.</summary>
        public long listened_seconds { get; set; }
        public int show_count { get; set; }

        /// <summary>
        /// The artist's show with the most listened time in the window, ties going to
        /// more plays, then the most recent play. Null only in a rare race: its source
        /// was deleted, or its plays were not visible, while the window was queried.
        /// </summary>
        public LiveSummaryShow? top_show { get; set; }

        /// <summary>plays / total_plays.</summary>
        public double share { get; set; }

        /// <summary>
        /// Average plays in the same window at the same hour of the week over the
        /// previous four weeks.
        /// </summary>
        public double expected_plays { get; set; }
        public double surprise { get; set; }
    }

    public class LiveSummaryOnThisDay
    {
        /// <summary>MM-DD that show dates were matched against.</summary>
        public string month_day { get; set; } = string.Empty;

        /// <summary>Plays of every show in the window with a full date on month_day.</summary>
        public long plays { get; set; }
        public int show_count { get; set; }

        /// <summary>
        /// The matching shows that also appear in <see cref="LiveSummary.shows"/>, in that
        /// list's order, so a client can render them from rows it already has.
        /// </summary>
        public IReadOnlyList<Guid> show_uuids { get; set; } = new List<Guid>();
    }
}
