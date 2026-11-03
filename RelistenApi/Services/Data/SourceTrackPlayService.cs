using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Services.Live;

namespace Relisten.Data
{
    public class SourceTrackPlaysService : RelistenDataServiceBase
    {
        public SourceTrackPlaysService(DbService db)
            : base(db) { }

        public async Task<SourceTrackPlay> RecordPlayedTrack(SourceTrackPlay track)
        {
            return await db.WithWriteConnection(con =>
                con.QuerySingleAsync<SourceTrackPlay>(
                    @"
				INSERT INTO
					source_track_plays

					(
						source_track_uuid,
						user_uuid,
						app_type
					)
				VALUES
					(
						@source_track_uuid,
						@user_uuid,
						@app_type
					)

				RETURNING *
			",
                    track
                )
            );
        }

        public async Task<IEnumerable<SourceTrackPlay>> PlayedTracksSince(
            int? lastSeenId = null,
            int limit = 2000
        )
        {
            var tracks = await db.WithConnection(con =>
                con.QueryAsync<SourceTrackPlay>(
                    $@"
				SELECT
					t.*
				FROM
					source_track_plays t
				WHERE
					t.created_at > now() - interval '1 hour'
					{(lastSeenId != null ? "AND t.id > @lastSeenId" : "")}
				ORDER BY
					t.created_at {(lastSeenId != null ? "" : "DESC")}, t.id {(lastSeenId != null ? "" : "DESC")}
				LIMIT
					@limit
			",
                    new { lastSeenId, limit }
                )
            );

            return tracks.OrderBy(t => t.id);
        }

        /// <summary>
        /// Groups the plays in a live window by app type, minute, show and artist, and
        /// attaches each show's and artist's baseline from the continuous aggregates. One
        /// round trip; each statement repeats the window join because a read replica
        /// cannot hold a temporary table between them.
        /// </summary>
        public async Task<LiveWindowPlays> PlaysInLiveWindow(LiveWindow window)
        {
            var parameters = new
            {
                windowStart = window.Start,
                windowEnd = window.End,
                showBaselineStart = window.Start.AddDays(-LiveSummaryRanking.ShowBaselineDays),
                dailyLagStart = window.Start.AddDays(-DailyAggregateMaxLagDays),
                fallbackTrackSeconds = FallbackTrackSeconds,
                baselineWeeks = LiveSummaryRanking.ArtistBaselineWeeks,
                // The outer edges of every prior same-hour-of-week window. The start is an
                // hour earlier so the hourly bucket overlapping the oldest window's start
                // is included. These constant bounds let the planner exclude chunks.
                artistBaselineStart = window
                    .Start.AddDays(-7 * LiveSummaryRanking.ArtistBaselineWeeks)
                    .AddHours(-1),
                artistBaselineEnd = window.End.AddDays(-7),
            };

            return await db.WithConnection(async con =>
            {
                using var results = await con.QueryMultipleAsync(
                    string.Join(
                        ";\n",
                        LiveAppTypesSql,
                        LiveTimelineSql,
                        LiveShowsSql,
                        LiveArtistsSql
                    ),
                    parameters
                );

                return new LiveWindowPlays
                {
                    AppTypes = (await results.ReadAsync<LiveAppTypeRow>()).ToList(),
                    Timeline = (await results.ReadAsync<LiveTimelineRow>()).ToList(),
                    Shows = (await results.ReadAsync<LiveShowRow>()).ToList(),
                    Artists = (await results.ReadAsync<LiveArtistRow>()).ToList(),
                };
            });
        }

        /// <summary>
        /// The daily aggregate's policy materializes whole days up to about three days
        /// behind; one more day of slack covers a late or skipped daily job. The hourly
        /// aggregate fills in plays from that span.
        /// </summary>
        private const int DailyAggregateMaxLagDays = 4;

        /// <summary>
        /// Counted for a track with no recorded duration when summing listened time;
        /// roughly the site-wide average track length.
        /// </summary>
        private const int FallbackTrackSeconds = 7 * 60;

        // Plays in [@windowStart, @windowEnd) joined to the catalog the way the
        // source_track_plays_hourly/_daily continuous aggregates are (see
        // Migrations/timescaledb_migration.sql), so plays whose track, source or show was
        // deleted drop out. The created_at range selects one or two daily chunks.
        private const string LiveWindowPlaysCte =
            @"
            WITH window_plays AS (
                SELECT
                    p.id,
                    p.created_at,
                    p.app_type,
                    date_trunc('minute', p.created_at) AS bucket_start,
                    t.uuid AS track_uuid,
                    t.title AS track_title,
                    t.slug AS track_slug,
                    t.track_position,
                    t.duration AS track_duration,
                    coalesce(t.duration, @fallbackTrackSeconds) AS track_seconds,
                    s.id AS source_id,
                    s.created_at AS source_created_at,
                    sh.id AS show_id,
                    sh.uuid AS show_uuid,
                    sh.display_date,
                    a.id AS artist_id,
                    a.uuid AS artist_uuid,
                    a.name AS artist_name,
                    a.slug AS artist_slug
                FROM
                    source_track_plays p
                    JOIN source_tracks t ON t.uuid = p.source_track_uuid
                    JOIN sources s ON s.id = t.source_id
                    JOIN shows sh ON sh.id = s.show_id
                    JOIN artists a ON a.id = t.artist_id
                WHERE
                    p.created_at >= @windowStart
                    AND p.created_at < @windowEnd
            )";

        private const string LiveAppTypesSql =
            LiveWindowPlaysCte
            + @"
            SELECT app_type, count(*) AS plays
            FROM window_plays
            GROUP BY app_type
            ORDER BY plays DESC, app_type";

        private const string LiveTimelineSql =
            LiveWindowPlaysCte
            + @",
            minute_counts AS (
                SELECT bucket_start, count(*) AS plays
                FROM window_plays
                GROUP BY bucket_start
            )
            SELECT b.bucket_start, coalesce(mc.plays, 0) AS plays
            FROM
                generate_series(
                    @windowStart::timestamptz,
                    @windowEnd::timestamptz - interval '1 minute',
                    interval '1 minute'
                ) AS b(bucket_start)
                LEFT JOIN minute_counts mc ON mc.bucket_start = b.bucket_start
            ORDER BY b.bucket_start";

        private const string LiveShowsSql =
            LiveWindowPlaysCte
            + @",
            show_totals AS (
                SELECT
                    show_id,
                    show_uuid,
                    artist_id,
                    display_date,
                    count(*) AS plays,
                    count(DISTINCT source_id) AS source_count,
                    min(created_at) AS first_played_at,
                    max(created_at) AS last_played_at,
                    max(id) AS last_play_id,
                    sum(track_seconds) AS listened_seconds
                FROM window_plays
                GROUP BY show_id, show_uuid, artist_id, display_date
            ),
            -- The representative source is the one played most in the window; ties go to
            -- the most recently played.
            representative_sources AS (
                SELECT DISTINCT ON (show_id) show_id, source_id, source_created_at
                FROM (
                    SELECT
                        show_id,
                        source_id,
                        source_created_at,
                        count(*) AS plays,
                        max(created_at) AS last_played_at
                    FROM window_plays
                    GROUP BY show_id, source_id, source_created_at
                ) source_plays
                ORDER BY show_id, plays DESC, last_played_at DESC, source_id
            ),
            -- The newest play of each show, for latest_track.
            latest_plays AS (
                SELECT DISTINCT ON (show_id)
                    show_id,
                    track_uuid,
                    track_title,
                    track_slug,
                    track_position,
                    track_duration,
                    created_at AS track_played_at
                FROM window_plays
                ORDER BY show_id, created_at DESC, id DESC
            ),
            show_minute_counts AS (
                SELECT show_id, bucket_start, count(*) AS plays
                FROM window_plays
                GROUP BY show_id, bucket_start
            ),
            -- Zero-filled per-minute counts, aligned with the buckets of LiveTimelineSql.
            show_timelines AS (
                SELECT
                    st.show_id,
                    array_agg(coalesce(mc.plays, 0)::int ORDER BY b.bucket_start) AS timeline
                FROM
                    show_totals st
                    CROSS JOIN generate_series(
                        @windowStart::timestamptz,
                        @windowEnd::timestamptz - interval '1 minute',
                        interval '1 minute'
                    ) AS b(bucket_start)
                    LEFT JOIN show_minute_counts mc
                        ON mc.show_id = st.show_id AND mc.bucket_start = b.bucket_start
                GROUP BY st.show_id
            ),
            -- The daily aggregate is refreshed once a day and trails by up to about three
            -- days, which would make every new show look unplayed. The hourly aggregate,
            -- refreshed every hour, fills the days after the last materialized daily
            -- bucket. Production has plays every day, so the latest bucket with rows is
            -- the materialized edge.
            daily_covered_until AS (
                SELECT max(play_day) + interval '1 day' AS covered_until
                FROM source_track_plays_daily
                WHERE play_day >= @dailyLagStart AND play_day < @windowStart
            ),
            -- Plays in the 30 days before the window. One scan of the daily aggregate
            -- (about 1M rows, 300 ms on production) beats per-show index probes, because
            -- popular shows have thousands of per-track daily rows. The window's own plays
            -- never count: the hourly branch ends at the window start, and the daily
            -- aggregate never materializes the current day.
            -- The hourly branch repeats @dailyLagStart as a constant so the planner can
            -- exclude older chunks; covered_until alone is only known at run time.
            show_baselines AS (
                SELECT show_uuid, sum(plays) AS plays_30d
                FROM (
                    SELECT d.show_uuid, d.plays
                    FROM source_track_plays_daily d
                    WHERE
                        d.play_day >= @showBaselineStart
                        AND d.play_day < @windowStart
                        AND d.show_uuid IN (SELECT show_uuid FROM show_totals)
                    UNION ALL
                    SELECT h.show_uuid, h.plays
                    FROM source_track_plays_hourly h
                    WHERE
                        h.play_hour >= coalesce(
                            (SELECT covered_until FROM daily_covered_until),
                            @dailyLagStart
                        )
                        AND h.play_hour >= @dailyLagStart
                        AND h.play_hour < @windowStart
                        AND h.show_uuid IN (SELECT show_uuid FROM show_totals)
                ) baseline_plays
                GROUP BY show_uuid
            )
            SELECT
                st.*,
                coalesce(b.plays_30d, 0) AS plays_30d,
                rs.source_id,
                rs.source_created_at,
                lp.track_uuid,
                lp.track_title,
                lp.track_slug,
                lp.track_position,
                lp.track_duration,
                lp.track_played_at,
                tl.timeline
            FROM
                show_totals st
                JOIN representative_sources rs ON rs.show_id = st.show_id
                JOIN latest_plays lp ON lp.show_id = st.show_id
                JOIN show_timelines tl ON tl.show_id = st.show_id
                LEFT JOIN show_baselines b ON b.show_uuid = st.show_uuid";

        private const string LiveArtistsSql =
            LiveWindowPlaysCte
            + @",
            artist_totals AS (
                SELECT
                    artist_id,
                    artist_uuid,
                    artist_name,
                    artist_slug,
                    count(*) AS plays,
                    count(DISTINCT show_id) AS show_count,
                    sum(track_seconds) AS listened_seconds
                FROM window_plays
                GROUP BY artist_id, artist_uuid, artist_name, artist_slug
            ),
            -- The same window at the same hour of the week, 1..@baselineWeeks weeks back.
            prior_windows AS (
                SELECT
                    @windowStart::timestamptz - weeks_ago * interval '7 days' AS start_at,
                    @windowEnd::timestamptz - weeks_ago * interval '7 days' AS end_at
                FROM generate_series(1, @baselineWeeks) AS weeks_ago
            ),
            -- Each hourly bucket counts in proportion to how much of it overlaps a prior
            -- window. The outer play_hour range repeats what the join implies; without it
            -- the planner cannot exclude chunks and the query took 1.8 s instead of 45 ms.
            artist_baselines AS (
                SELECT
                    h.artist_uuid,
                    sum(
                        h.plays * extract(
                            epoch FROM least(h.play_hour + interval '1 hour', w.end_at)
                                - greatest(h.play_hour, w.start_at)
                        ) / 3600.0
                    ) / @baselineWeeks AS expected_plays
                FROM
                    prior_windows w
                    JOIN source_track_plays_hourly h
                        ON h.play_hour > w.start_at - interval '1 hour'
                        AND h.play_hour < w.end_at
                WHERE
                    h.play_hour >= @artistBaselineStart
                    AND h.play_hour < @artistBaselineEnd
                    AND h.artist_uuid IN (SELECT artist_uuid FROM artist_totals)
                GROUP BY h.artist_uuid
            )
            SELECT at.*, coalesce(b.expected_plays, 0)::float8 AS expected_plays
            FROM
                artist_totals at
                LEFT JOIN artist_baselines b ON b.artist_uuid = at.artist_uuid";
    }
}
