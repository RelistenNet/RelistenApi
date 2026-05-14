using System;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class SearchService : RelistenDataServiceBase
    {
        public SearchService(DbService db) : base(db)
        {
        }

        public async Task<SearchResults> Search(string searchTerm, Guid? artistUuid = null, SearchOptions? options = null)
        {
            options ??= new SearchOptions();

            return await db.WithConnection(async con =>
            {
                var parms = new {searchTerm, artistUuid};

                return new SearchResults
                {
                    Artists = options.Artists ? await con.QueryAsync<SlimArtist>($@"
						SELECT
							artists.*
						FROM
							artists
							LEFT JOIN (
								SELECT artist_id, COUNT(*) as show_count FROM shows GROUP BY artist_id
							) show_counts ON show_counts.artist_id = artists.id
							WHERE
								name ILIKE '%' || @searchTerm || '%'
								{(artistUuid.HasValue ? "AND artists.uuid = @artistUuid" : "")}
						ORDER BY
							COALESCE(show_counts.show_count, 0) DESC, name
						LIMIT 20;
					", parms) : Array.Empty<SlimArtist>(),
                    Shows = options.Shows ? await con.QueryAsync<ShowWithSlimArtist, SlimArtist, ShowWithSlimArtist>($@"
						SELECT
								s.*,
								a.uuid as artist_uuid,
								cnt.max_updated_at as most_recent_source_updated_at,
								COALESCE(cnt.source_count, 0) as source_count,
								COALESCE(cnt.has_soundboard_source, false) as has_soundboard_source,
								COALESCE(cnt.has_flac, false) as has_streamable_flac_source,
								v.uuid as venue_uuid,
								t.uuid as tour_uuid,
								y.uuid as year_uuid,
								a.*
							FROM
								shows s
								JOIN artists a ON s.artist_id = a.id
								LEFT JOIN show_source_information cnt ON cnt.show_id = s.id
								LEFT JOIN venues v ON s.venue_id = v.id
								LEFT JOIN tours t ON s.tour_id = t.id
								LEFT JOIN years y ON s.year_id = y.id
						WHERE
							s.display_date ILIKE '%' || @searchTerm || '%'
							{(artistUuid.HasValue ? "AND a.uuid = @artistUuid" : "")}
						ORDER BY
							COALESCE(cnt.source_count, 0) DESC, s.display_date DESC
						LIMIT 20;
					", (s, a) =>
                    {
                        s.slim_artist = a;
                        return s;
                    }, parms) : Array.Empty<ShowWithSlimArtist>(),
                    Songs = options.Songs ? await con.QueryAsync<SetlistSongWithSlimArtist, SlimArtist, SetlistSongWithSlimArtist>($@"
		                SELECT
			                    s.*,
			                    a.uuid as artist_uuid,
			                    COUNT(shows.id) as shows_played_at,
			                    a.*
		                FROM
		                    setlist_songs s
		                    LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
		                    LEFT JOIN setlist_shows set_shows ON set_shows.id = p.played_setlist_show_id
		                    JOIN shows shows ON shows.date = set_shows.date AND shows.artist_id = s.artist_id
							JOIN artists a ON s.artist_id = a.id
							WHERE
								s.name ILIKE '%' || @searchTerm || '%'
								{(artistUuid.HasValue ? "AND a.uuid = @artistUuid" : "")}
		                GROUP BY
		                    a.id, s.id
		                ORDER BY shows_played_at DESC, s.name
						LIMIT 20;
					", (s, a) =>
                    {
                        s.slim_artist = a;
                        return s;
                    }, parms) : Array.Empty<SetlistSongWithSlimArtist>(),
                    Sources = options.Sources ? await con.QueryAsync<SourceWithSlimArtist, SlimArtist, SourceWithSlimArtist>($@"
						SELECT
								s.*,
								a.uuid as artist_uuid,
								sh.uuid as show_uuid,
								v.uuid as venue_uuid,
								a.*
							FROM
								sources s
								JOIN artists a ON s.artist_id = a.id
								LEFT JOIN shows sh ON sh.id = s.show_id
								LEFT JOIN venues v ON v.id = s.venue_id
						WHERE
							(s.upstream_identifier ILIKE '%' || @searchTerm || '%'
							OR s.description ILIKE '%' || @searchTerm || '%'
							OR s.taper_notes ILIKE '%' || @searchTerm || '%'
							OR s.source ILIKE '%' || @searchTerm || '%'
							OR s.taper ILIKE '%' || @searchTerm || '%'
							OR s.transferrer ILIKE '%' || @searchTerm || '%'
							OR s.lineage ILIKE '%' || @searchTerm || '%')
							{(artistUuid.HasValue ? "AND a.uuid = @artistUuid" : "")}
						ORDER BY
							s.avg_rating_weighted DESC, s.duration DESC, s.display_date DESC
						LIMIT 20;
					", (s, a) =>
                    {
                        s.slim_artist = a;
                        return s;
                    }, parms) : Array.Empty<SourceWithSlimArtist>(),
                    Tours = options.Tours ? await con.QueryAsync<TourWithSlimArtist, SlimArtist, TourWithSlimArtist>($@"
						SELECT
								t.*,
								a.uuid as artist_uuid,
								a.*
						FROM
							tours t
							JOIN artists a ON t.artist_id = a.id
							LEFT JOIN shows sh ON sh.tour_id = t.id
						WHERE
							t.name ILIKE '%' || @searchTerm || '%'
							{(artistUuid.HasValue ? "AND a.uuid = @artistUuid" : "")}
						GROUP BY
							t.id, a.id
						ORDER BY
							COUNT(sh.id) DESC, t.start_date DESC, t.name
					    LIMIT 20;
					", (t, a) =>
                    {
                        t.slim_artist = a;
                        return t;
                    }, parms) : Array.Empty<TourWithSlimArtist>(),
                    Venues = options.Venues ? await con.QueryAsync<VenueWithSlimArtist, SlimArtist, VenueWithSlimArtist>($@"
						SELECT
								v.*,
								a.uuid as artist_uuid,
								a.*
						FROM
							venues v
							JOIN artists a ON v.artist_id = a.id
							LEFT JOIN shows sh ON sh.venue_id = v.id
						WHERE
							(v.name ILIKE '%' || @searchTerm || '%'
							OR v.location ILIKE '%' || @searchTerm || '%'
					        OR v.past_names ILIKE '%' || @searchTerm || '%')
							{(artistUuid.HasValue ? "AND a.uuid = @artistUuid" : "")}
						GROUP BY
							v.id, a.id
						ORDER BY
							COUNT(sh.id) DESC, v.name
						LIMIT 20;
					", (v, a) =>
                    {
                        v.slim_artist = a;
                        return v;
                    }, parms) : Array.Empty<VenueWithSlimArtist>()
                };
            });
        }
    }
}
