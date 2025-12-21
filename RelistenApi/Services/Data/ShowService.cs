using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class ShowService : RelistenDataServiceBase
    {
        public ShowService(
            DbService db,
            SourceService sourceService
        ) : base(db)
        {
            _sourceService = sourceService;
        }

        private SourceService _sourceService { get; }

        public async Task<IEnumerable<T>> ShowsForCriteriaGeneric<T>(
            Artist? artist,
            string where,
            object? parms,
            int? limit = null,
            string? orderBy = null,
            bool includeNestedObject = true
        ) where T : Show
        {
            orderBy ??= "display_date ASC";
            var limitClause = limit == null ? "" : "LIMIT " + limit;

            return await db.WithConnection(con => con.QueryAsync<T, VenueWithShowCount, Tour, Era, Year, T>(@"
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
                    " + where + @"
                ORDER BY
                    " + orderBy + @"
				" + limitClause + @"
            ", (show, venue, tour, era, year) =>
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
            }, parms));
        }

        public async Task<IEnumerable<Show>> ShowsForCriteria(
            Artist? artist,
            string where,
            object? parms,
            int? limit = null,
            string? orderBy = null,
            bool includeNestedObject = true)
        {
            return await ShowsForCriteriaGeneric<Show>(artist, where, parms, limit, orderBy, includeNestedObject);
        }

        public async Task<IEnumerable<ShowWithArtist>> ShowsForCriteriaWithArtists(string where, object? parms,
            int? limit = null, string? orderBy = null)
        {
            orderBy = orderBy == null ? "display_date ASC" : orderBy;
            var limitClause = limit == null ? "" : "LIMIT " + limit;

            return await db.WithConnection(con =>
                con.QueryAsync<ShowWithArtist, VenueWithShowCount, Tour, Era, Artist, Features, Year, ShowWithArtist>(@"
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
                        " + where + @"
                    ORDER BY
                        " + orderBy + @"
                    " + limitClause + @"
                ", (show, venue, tour, era, art, features, year) =>
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
                }, parms));
        }

        public async Task<IEnumerable<ShowWithArtist>> RecentlyPerformed(IReadOnlyList<Artist>? artists = null,
            int? shows = null, int? days = null)
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
                    return await ShowsForCriteriaWithArtists($@"
                        s.artist_id = ANY(@artistIds)
                        AND s.date > (CURRENT_DATE - INTERVAL '{days}' day)
                    ", new {artistIds = artists.Select(a => a.id).ToList()}, null, "s.display_date DESC");
                }

                return await ShowsForCriteriaWithArtists($@"
                    s.date > (CURRENT_DATE - INTERVAL '{days}' day)
                ", new { }, null, "s.display_date DESC");
            }

            if (artists != null)
            {
                return await ShowsForCriteriaWithArtists(@"
                    s.artist_id = ANY(@artistIds)
                ", new {artistIds = artists.Select(a => a.id).ToList()}, shows, "s.display_date DESC");
            }

            return await ShowsForCriteriaWithArtists(@"
            ", new { }, shows, "s.display_date DESC");
        }

        public async Task<IEnumerable<ShowWithArtist>> RecentlyUpdated(IReadOnlyList<Artist>? artists = null,
            int? shows = null, int? days = null)
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
                    return await ShowsForCriteriaWithArtists($@"
                        s.artist_id = ANY(@artistIds)
                        AND s.updated_at > (CURRENT_DATE - INTERVAL '{days}' day)
                    ", new {artistIds = artists.Select(a => a.id).ToList()}, null, "s.updated_at DESC");
                }

                return await ShowsForCriteriaWithArtists($@"
                    s.updated_at > (CURRENT_DATE - INTERVAL '{days}' day)
                ", new { }, null, "s.updated_at DESC");
            }

            if (artists != null)
            {
                return await ShowsForCriteriaWithArtists(@"
                    s.artist_id = ANY(@artistIds)
                ", new {artistIds = artists.Select(a => a.id).ToList()}, shows, "s.updated_at DESC");
            }

            return await ShowsForCriteriaWithArtists(@"
            ", new { }, shows, "s.updated_at DESC");
        }

        public Task<ShowWithSources?> ShowWithSourcesForArtistOnDate(Artist artist, string displayDate)
        {
            return ShowWithSourcesForGeneric(
                artist,
                "s.artist_id = @artistId AND s.display_date = @showDate",
                new {artistId = artist.id, showDate = displayDate}
            );
        }

        public Task<ShowWithSources?> ShowWithSourcesForUuid(Artist artist, Guid uuid)
        {
            return ShowWithSourcesForGeneric(
                artist,
                "s.artist_id = @artistId AND s.uuid = @uuid",
                new {artistId = artist.id, uuid}
            );
        }

        public async Task<ShowWithSources?> ShowWithSourcesForGeneric(Artist artist, string where, object param)
        {
            var shows = await ShowsForCriteriaGeneric<ShowWithSources>(artist, where, param);
            var show = shows.FirstOrDefault();

            if (show == null)
            {
                return null;
            }

            show.sources = await _sourceService.FullSourcesForShow(artist, show);

            return show;
        }

        public async Task<IEnumerable<Show>> AllSimpleForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Show>(@"
                SELECT
                    id, created_at, updated_at, date
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ", new {artist.id}));
        }
    }
}
