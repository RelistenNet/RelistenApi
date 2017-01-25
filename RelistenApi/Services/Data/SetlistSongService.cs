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
                    *
                FROM
                    setlist_songs
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = ANY(@upstreamIds)
            ", new { artistId = artist.id, upstreamIds = upstreamIds.ToList() }));
        }

        public async Task<IEnumerable<SetlistSong>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<SetlistSong>(@"
                SELECT
                    *
                FROM
                    setlist_songs 
                WHERE
                    artist_id = @id
            ", artist));
        }

        public async Task<SetlistSongWithShows> ForIdWithShows(Artist artist, int id)
        {
            SetlistSongWithShows bigSong = null;
            await db.WithConnection(con => con.QueryAsync<SetlistSongWithShows, Show, Venue, Tour, Era, SetlistSongWithShows>(@"
                SELECT
                    s.*, shows.*, v.*, t.*
                FROM
                    setlist_songs s
                    LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
                    LEFT JOIN setlist_shows set_shows ON set_shows.id = p.played_setlist_show_id
                    JOIN shows shows ON shows.date = set_shows.date AND shows.artist_id = @artistId
                    LEFT JOIN venues v ON shows.venue_id = v.id
                    LEFT JOIN tours t ON shows.tour_id = t.id
                    LEFT JOIN eras e ON shows.era_id = e.id
                WHERE
                    s.artist_id = @artistId
                    AND s.id = @songId
                ORDER BY shows.date
                ",
                (song, show, venue, tour, era) => {
                    if(bigSong == null) {
                        bigSong = song;
                        bigSong.shows = new List<Show>();
                    }

                    show.venue = venue;
                    show.tour = tour;
                    show.era = era;

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
                    s.*, COUNT(shows.id) as shows_played_at
                FROM
                    setlist_songs s
                    LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
                    LEFT JOIN setlist_shows set_shows ON set_shows.id = p.played_setlist_show_id
                    JOIN shows shows ON shows.date = set_shows.date AND shows.artist_id = @id
                WHERE
                    s.artist_id = @id
                GROUP BY
                    s.id
                ORDER BY name
            ", artist));
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
                    inserted.Add(await con.QuerySingleAsync<SetlistSong>(@"
                        INSERT INTO
                            setlist_songs

                            (
                                artist_id,
                                name,
                                slug,
                                upstream_identifier,
                                updated_at
                            )
                        VALUES
                            (
                                @artist_id,
                                @name,
                                @slug,
                                @upstream_identifier,
                                @updated_at
                            )
                        RETURNING *
                    ", song));
                }

                return inserted;
            });
        }
    }
}