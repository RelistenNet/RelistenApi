using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace Relisten.Data
{
    public class ShowService : RelistenDataServiceBase
    {
        // These labels provide a repeatable presentation order, not a claim about start times.
        private const string PerformanceOrder =
            "CASE s.performance_type WHEN 'early' THEN 1 WHEN 'afternoon' THEN 2 "
            + "WHEN 'late' THEN 3 WHEN 'other' THEN 4 ELSE 5 END, s.uuid";

        public ShowService(
            DbService db,
            SourceService sourceService,
            Lazy<PopularityService> popularityService
        )
            : base(db)
        {
            _sourceService = sourceService;
            this.popularityService = popularityService;
        }

        private SourceService _sourceService { get; }

        // Ingestion uses catalog writes without constructing API-only background job dependencies.
        private readonly Lazy<PopularityService> popularityService;

        public async Task<IEnumerable<T>> ShowsForCriteriaGeneric<T>(
            Artist? artist,
            string where,
            object? parms,
            int? limit = null,
            string? orderBy = null,
            bool includeNestedObject = true
        )
            where T : Show
        {
            orderBy = (orderBy ?? "s.display_date ASC") + ", " + PerformanceOrder;
            var limitClause = limit == null ? "" : "LIMIT " + limit;

            var shows = (
                await db.WithConnection(con =>
                    con.QueryAsync<T, VenueWithShowCount, Tour, Era, Year, T>(
                        @"
                SELECT
                    s.*,
                    a.uuid as artist_uuid,
                    cnt.max_updated_at as most_recent_source_updated_at,
					cnt.source_count,
                    cnt.has_soundboard_source,
                    cnt.has_flac as has_streamable_flac_source,
                    v.uuid as venue_uuid,
                    t.uuid as tour_uuid,
                    y.uuid as year_uuid,
					v.*,
                    a.uuid as artist_uuid,
                    COALESCE(venue_counts.shows_at_venue, 0) as shows_at_venue,
					t.*,
                    a.uuid as artist_uuid,
					e.*,
                    a.uuid as artist_uuid,
                    y.*,
                    a.uuid as artist_uuid
                FROM
                    shows s
                    JOIN artists a ON s.artist_id = a.id
                    LEFT JOIN venues v ON s.venue_id = v.id
                    LEFT JOIN tours t ON s.tour_id = t.id
                    LEFT JOIN eras e ON s.era_id = e.id
                    LEFT JOIN years y ON s.year_id = y.id

                    INNER JOIN show_source_information cnt ON cnt.show_id = s.id
                    LEFT JOIN venue_show_counts venue_counts ON venue_counts.id = s.venue_id

                WHERE
                    "
                            + where
                            + @"
                ORDER BY
                    "
                            + orderBy
                            + @"
				"
                            + limitClause
                            + @"
            ",
                        (show, venue, tour, era, year) =>
                        {
                            if (!includeNestedObject)
                            {
                                return show;
                            }

                            show.venue = venue;

                            if (artist == null || artist.features.tours)
                            {
                                show.tour = tour;
                            }

                            if (artist == null || artist.features.eras)
                            {
                                show.era = era;
                            }

                            if (artist == null || artist.features.years)
                            {
                                show.year = year;
                            }

                            return show;
                        },
                        parms
                    )
                )
            ).ToList();

            await ApplyShowPopularity(shows, artist);
            return shows;
        }

        public async Task<IEnumerable<Show>> ShowsForCriteria(
            Artist? artist,
            string where,
            object? parms,
            int? limit = null,
            string? orderBy = null,
            bool includeNestedObject = true
        )
        {
            return await ShowsForCriteriaGeneric<Show>(
                artist,
                where,
                parms,
                limit,
                orderBy,
                includeNestedObject
            );
        }

        public async Task<IEnumerable<ShowWithArtist>> ShowsForCriteriaWithArtists(
            string where,
            object? parms,
            int? limit = null,
            string? orderBy = null
        )
        {
            orderBy = (orderBy ?? "s.display_date ASC") + ", " + PerformanceOrder;
            var limitClause = limit == null ? "" : "LIMIT " + limit;

            var shows = (
                await db.WithConnection(con =>
                    con.QueryAsync<
                        ShowWithArtist,
                        VenueWithShowCount,
                        Tour,
                        Era,
                        Artist,
                        Features,
                        Year,
                        ShowWithArtist
                    >(
                        @"
                    SELECT
                        s.*,
                        a.uuid as artist_uuid,
                        cnt.max_updated_at as most_recent_source_updated_at,
						cnt.source_count,
						cnt.has_soundboard_source,
                        cnt.has_flac as has_streamable_flac_source,
                        v.uuid as venue_uuid,
                        t.uuid as tour_uuid,
                        y.uuid as year_uuid,
					    v.*,
                        a.uuid as artist_uuid,
                        COALESCE(venue_counts.shows_at_venue, 0) as shows_at_venue,
					    t.*,
                        a.uuid as artist_uuid,
					    e.*,
                        a.uuid as artist_uuid,
                        a.*,
                        y.*,
                        a.uuid as artist_uuid
                    FROM
                        shows s
                        LEFT JOIN venues v ON s.venue_id = v.id
                        LEFT JOIN tours t ON s.tour_id = t.id
                        LEFT JOIN eras e ON s.era_id = e.id
                        LEFT JOIN years y ON s.year_id = y.id
                        LEFT JOIN (
                        	SELECT
                        		arts.id as aid, arts.*, f.*
                        	FROM
                        		artists arts
                        		INNER JOIN features f ON f.artist_id = arts.id
                        ) a ON s.artist_id = a.aid

                        INNER JOIN show_source_information cnt ON cnt.show_id = s.id
                        LEFT JOIN venue_show_counts venue_counts ON venue_counts.id = s.venue_id
                    WHERE
                        "
                            + where
                            + @"
                    ORDER BY
                        "
                            + orderBy
                            + @"
                    "
                            + limitClause
                            + @"
                ",
                        (show, venue, tour, era, art, features, year) =>
                        {
                            art.features = features;
                            show.artist = art;
                            show.venue = venue;
                            show.year = year;

                            if (art.features.tours)
                            {
                                show.tour = tour;
                            }

                            if (art.features.eras)
                            {
                                show.era = era;
                            }

                            return show;
                        },
                        parms
                    )
                )
            ).ToList();

            await ApplyShowPopularity(shows, null);
            return shows;
        }

        public async Task<IEnumerable<ShowWithArtist>> RecentlyPerformed(
            IReadOnlyList<Artist>? artists = null,
            int? shows = null,
            int? days = null
        )
        {
            if (shows == null && days == null)
            {
                shows = 25;
            }

            if (shows > 250)
            {
                shows = 250;
            }

            if (days > 90)
            {
                days = 90;
            }

            if (days.HasValue)
            {
                if (artists != null)
                {
                    return await ShowsForCriteriaWithArtists(
                        $@"
                        s.artist_id = ANY(@artistIds)
                        AND s.date > (CURRENT_DATE - INTERVAL '{days}' day)
                    ",
                        new { artistIds = artists.Select(a => a.id).ToList() },
                        null,
                        "s.display_date DESC"
                    );
                }

                return await ShowsForCriteriaWithArtists(
                    $@"
                    s.date > (CURRENT_DATE - INTERVAL '{days}' day)
                ",
                    new { },
                    null,
                    "s.display_date DESC"
                );
            }

            if (artists != null)
            {
                return await ShowsForCriteriaWithArtists(
                    @"
                    s.artist_id = ANY(@artistIds)
                ",
                    new { artistIds = artists.Select(a => a.id).ToList() },
                    shows,
                    "s.display_date DESC"
                );
            }

            return await ShowsForCriteriaWithArtists(
                @"
            ",
                new { },
                shows,
                "s.display_date DESC"
            );
        }

        public async Task<IEnumerable<ShowWithArtist>> RecentlyUpdated(
            IReadOnlyList<Artist>? artists = null,
            int? shows = null,
            int? days = null
        )
        {
            if (shows == null && days == null)
            {
                shows = 25;
            }

            if (shows > 250)
            {
                shows = 250;
            }

            if (days > 90)
            {
                days = 90;
            }

            if (days.HasValue)
            {
                if (artists != null)
                {
                    return await ShowsForCriteriaWithArtists(
                        $@"
                        s.artist_id = ANY(@artistIds)
                        AND s.updated_at > (CURRENT_DATE - INTERVAL '{days}' day)
                    ",
                        new { artistIds = artists.Select(a => a.id).ToList() },
                        null,
                        "s.updated_at DESC"
                    );
                }

                return await ShowsForCriteriaWithArtists(
                    $@"
                    s.updated_at > (CURRENT_DATE - INTERVAL '{days}' day)
                ",
                    new { },
                    null,
                    "s.updated_at DESC"
                );
            }

            if (artists != null)
            {
                return await ShowsForCriteriaWithArtists(
                    @"
                    s.artist_id = ANY(@artistIds)
                ",
                    new { artistIds = artists.Select(a => a.id).ToList() },
                    shows,
                    "s.updated_at DESC"
                );
            }

            return await ShowsForCriteriaWithArtists(
                @"
            ",
                new { },
                shows,
                "s.updated_at DESC"
            );
        }

        /// <summary>
        /// Preserves the original date-route destination after a split. Dates created with
        /// separate unresolved recordings use their oldest show as a stable fallback.
        /// </summary>
        public Task<ShowWithSources?> ShowWithSourcesForArtistOnDate(
            Artist artist,
            string displayDate
        )
        {
            return ShowWithSourcesForGeneric(
                artist,
                "s.artist_id = @artistId AND s.display_date = @showDate",
                new { artistId = artist.id, showDate = displayDate },
                "s.is_legacy_survivor DESC, s.id ASC"
            );
        }

        /// <summary>
        /// Returns the separate recorded performances on an artist's literal display date.
        /// Partial dates stay literal; matching an unknown date does not merge performances.
        /// </summary>
        public async Task<IReadOnlyList<ShowWithSources>> PerformancesForArtistOnDate(
            Artist artist,
            string displayDate
        )
        {
            var shows = (
                await ShowsForCriteriaGeneric<ShowWithSources>(
                    artist,
                    "s.artist_id = @artistId AND s.display_date = @showDate",
                    new { artistId = artist.id, showDate = displayDate }
                )
            ).ToList();

            foreach (var show in shows)
            {
                show.sources = await _sourceService.FullSourcesForShow(artist, show);
            }

            return shows;
        }

        public Task<ShowWithSources?> ShowWithSourcesForUuid(Artist artist, Guid uuid)
        {
            return ShowWithSourcesForGeneric(
                artist,
                "s.artist_id = @artistId AND s.uuid = @uuid",
                new { artistId = artist.id, uuid }
            );
        }

        public async Task<ShowWithSources?> ShowWithSourcesForGeneric(
            Artist artist,
            string where,
            object param,
            string? orderBy = null
        )
        {
            var shows = await ShowsForCriteriaGeneric<ShowWithSources>(
                artist,
                where,
                param,
                limit: 1,
                orderBy: orderBy
            );
            var show = shows.FirstOrDefault();

            if (show == null)
            {
                return null;
            }

            show.sources = await _sourceService.FullSourcesForShow(artist, show);

            return show;
        }

        private async Task ApplyShowPopularity<T>(IReadOnlyList<T> shows, Artist? artist)
            where T : Show
        {
            if (shows.Count == 0)
            {
                return;
            }

            if (artist != null)
            {
                var showPopularity = await popularityService.Value.GetShowPopularityMapForArtist(
                    artist.uuid
                );
                popularityService.Value.ApplyShowPopularity(shows, showPopularity);
                return;
            }

            foreach (var group in shows.GroupBy(show => show.artist_uuid))
            {
                var showPopularity = await popularityService.Value.GetShowPopularityMapForArtist(
                    group.Key
                );
                popularityService.Value.ApplyShowPopularity(group.ToList(), showPopularity);
            }
        }

        public async Task<IEnumerable<int>> ActiveArtistIds(int days)
        {
            return await db.WithConnection(con =>
                con.QueryAsync<int>(
                    $@"
                SELECT DISTINCT artist_id
                FROM shows
                WHERE date > CURRENT_DATE - INTERVAL '{days}' day
            "
                )
            );
        }

        public async Task<IEnumerable<Show>> AllSimpleForArtist(Artist artist)
        {
            return await db.WithConnection(con =>
                con.QueryAsync<Show>(
                    @"
                SELECT
                    id, created_at, updated_at, date
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ",
                    new { artist.id }
                )
            );
        }
    }
}
