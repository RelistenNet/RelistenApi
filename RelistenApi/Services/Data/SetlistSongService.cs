using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class SetlistSongService : RelistenDataServiceBase
    {
        public SetlistSongService(DbService db) : base(db) { }

        public async Task<IEnumerable<SetlistSong>> ForUpstreamIdentifiers(Artist artist, IEnumerable<string> upstreamIds)
        {
            return await db.WithConnection(con => con.QueryAsync<SetlistSong>(@"
                SELECT
                    s.*
                    , a.uuid as artist_uuid
                FROM
                    setlist_songs s
                    JOIN artists a on s.artist_id = a.id
                WHERE
                    s.artist_id = @artistId
                    AND s.upstream_identifier = ANY(@upstreamIds)
            ", new { artistId = artist.id, upstreamIds = upstreamIds.ToList() }));
        }

        public async Task<IEnumerable<SetlistSong>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<SetlistSong>(@"
                SELECT
                    s.*
                    , a.uuid as artist_uuid
                FROM
                    setlist_songs s
                    JOIN artists a on s.artist_id = a.id
                WHERE
                    s.artist_id = @artistId
            ", new { artist.id }));
        }

        public async Task<SetlistSongWithShows> ForIdWithShows(Artist artist, int id)
        {
            SetlistSongWithShows bigSong = null;
            await db.WithConnection(con => con.QueryAsync<SetlistSongWithShows, Show, VenueWithShowCount, Tour, Era, Year, SetlistSongWithShows>(@"
                SELECT
                    s.*
                    , a.id as artist_uuid
                    , shows.*
                    , a.id as artist_uuid
                    , cnt.max_updated_at as most_recent_source_updated_at
                    , cnt.source_count
                    , cnt.has_soundboard_source
                    , v.*, t.*, e.*, y.*
                FROM
                    setlist_songs s
                    LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
                    LEFT JOIN setlist_shows set_shows ON set_shows.id = p.played_setlist_show_id
                    JOIN shows shows ON shows.date = set_shows.date AND shows.artist_id = @artistId
                    LEFT JOIN venues v ON shows.venue_id = v.id
                    LEFT JOIN tours t ON shows.tour_id = t.id
                    LEFT JOIN eras e ON shows.era_id = e.id
                    LEFT JOIN years y ON shows.year_id = y.id
                    JOIN artists a ON a.id = shows.artist_id

                    INNER JOIN (
                        SELECT
                            src.show_id,
                            MAX(src.updated_at) as max_updated_at,
                            COUNT(*) as source_count,
                            BOOL_OR(src.is_soundboard) as has_soundboard_source
                        FROM
                            sources src
                        GROUP BY
                            src.show_id
                    ) cnt ON cnt.show_id = shows.id
                WHERE
                    s.artist_id = @artistId
                    AND s.id = @songId
                ORDER BY shows.date
                ",
                (song, show, venue, tour, era, year) => {
                    if(bigSong == null) {
                        bigSong = song;
                        bigSong.shows = new List<Show>();
                    }

                    show.venue = venue;
                    show.tour = tour;
                    show.era = era;
                    show.year = year;

                    bigSong.shows.Add(show);

                    return song;
                },
                new { artistId = artist.id, songId = id }));

            return bigSong;
        }

        public async Task<IEnumerable<SetlistSongWithPlayCount>> AllForArtistWithPlayCount(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<SetlistSongWithPlayCount>(@"
                SELECT
                    s.*, a.id as artist_uuid, COUNT(shows.id) as shows_played_at
                FROM
                    setlist_songs s
                    LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
                    LEFT JOIN setlist_shows set_shows ON set_shows.id = p.played_setlist_show_id
                    JOIN shows shows ON shows.date = set_shows.date AND shows.artist_id = @id
                    JOIN artists a ON a.id = shows.artist_id
                WHERE
                    s.artist_id = @id
                GROUP BY
                    s.id
                ORDER BY s.name
            ", new { artist.id }));
        }

        public async Task<IEnumerable<SetlistSong>> InsertAll(Artist artist, IEnumerable<SetlistSong> songs)
        {
            /*if (song.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<SetlistSong>(@"
                    UPDATE
                        setlist_songs
                    SET
                        artist_id = @artist_id,
                        name = @name,
                        slug = @slug,
                        upstream_identifier = @upstream_identifier,
                        updated_at = @updated_at
                    WHERE
                        id = @id
                    RETURNING *
                ", song));
            }*/
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SetlistSong>();

                foreach (var song in songs)
                {
                    var p = new {
                        song.id,
                        song.artist_id,
                        song.name,
                        song.slug,
                        song.upstream_identifier,
                        song.updated_at,
                    };

                    inserted.Add(await con.QuerySingleAsync<SetlistSong>(@"
                        INSERT INTO
                            setlist_songs

                            (
                                artist_id,
                                name,
                                slug,
                                upstream_identifier,
                                updated_at,
                                uuid
                            )
                        VALUES
                            (
                                @artist_id,
                                @name,
                                @slug,
                                @upstream_identifier,
                                @updated_at,
                                md5(@artist_id || '::setlist_song::' || @upstream_identifier)::uuid
                            )
                        RETURNING *
                    ", p));
                }

                return inserted;
            });
        }
    }
}