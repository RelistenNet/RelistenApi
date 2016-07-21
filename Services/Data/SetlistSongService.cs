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

        public async Task<IEnumerable<SetlistSongWithPlayCount>> AllForArtistWithPlayCount(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<SetlistSongWithPlayCount>(@"
                SELECT
                    s.*, COUNT(p.played_setlist_show_id) as shows_played_at
                FROM
                    setlist_songs s
                    LEFT JOIN setlist_songs_plays p ON p.played_setlist_song_id = s.id
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