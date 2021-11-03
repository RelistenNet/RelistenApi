using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Relisten.Import;

namespace Relisten.Data
{
    public abstract class RelistenDataServiceBase
    {
        protected DbService db { get; set; }

        protected RelistenDataServiceBase(DbService db)
        {
            this.db = db;
        }
    }

    public class SetlistShowService : RelistenDataServiceBase
    {
        public SetlistShowService(DbService db) : base(db) { }

        public async Task<IEnumerable<SetlistShow>> AllForArtist(Artist artist, bool withVenuesToursAndEras = false)
        {
            if (withVenuesToursAndEras)
            {
                return await db.WithConnection(con => con.QueryAsync<SetlistShow, Tour, Venue, Era, SetlistShow>(@"
                    SELECT
                        s.*, a.uuid as artist_uuid, t.uuid as tour_uuid, v.uuid as venue_uuid, t.*, v.*, e.*
                    FROM
                        setlist_shows s
                        LEFT JOIN tours t ON s.tour_id = t.id
                        LEFT JOIN venues v ON s.venue_id = v.id
                        LEFT JOIN eras e ON s.era_id = e.id
                        LEFT JOIN artists a ON s.artist_id = a.id
                    WHERE
                        s.artist_id = @id
                    ", (setlistShow, tour, venue, era) =>
                {
                    setlistShow.venue = venue;

                    if (artist.features.tours)
                    {
                        setlistShow.tour = tour;
                    }

                    if (artist.features.eras)
                    {
                        setlistShow.era = era;
                    }

                    return setlistShow;
                }, new { artist.id }));
            }

            return await db.WithConnection(con => con.QueryAsync<SetlistShow>(@"
                SELECT
                    *
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ", new { artist.id }));
        }

        public async Task<IEnumerable<SimpleSetlistShow>> AllSimpleForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<SimpleSetlistShow>(@"
                SELECT
                    id, created_at, updated_at, date
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ", new { artist.id }));
        }

        public async Task<SetlistShow> Save(SetlistShow show)
        {
            var p = new {
                show.id,
                show.artist_id,
                show.venue_id,
                show.date,
                show.tour_id,
                show.upstream_identifier,
                show.updated_at,
            };

            if (show.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<SetlistShow>(@"
                    UPDATE
                        setlist_shows
                    SET
                        artist_id = @artist_id,
                        venue_id = @venue_id,
                        date = @date,
                        tour_id = @tour_id,
                        upstream_identifier = @upstream_identifier,
                        updated_at = @updated_at,
                        uuid = md5(@artist_id || '::setlist_show::' || @upstream_identifier)::uuid
                    WHERE
                        id = @id
                    RETURNING *
                ", p));
            }
            else
            {
                return await db.WithConnection(con => con.QuerySingleAsync<SetlistShow>(@"
                    INSERT INTO
                        setlist_shows

                        (
                            artist_id,
                            venue_id,
                            date,
                            tour_id,
                            upstream_identifier,
                            updated_at,
                            uuid
                        )
                    VALUES
                        (
                            @artist_id,
                            @venue_id,
                            @date,
                            @tour_id,
                            @upstream_identifier,
                            @updated_at,
                            md5(@artist_id || '::setlist_show::' || @upstream_identifier)::uuid
                        )
                    RETURNING *
                ", p));
            }
        }

        public async Task<ImportStats> UpdateSongPlays(SetlistShow show, IEnumerable<SetlistSong> songs)
        {
            var stats = new ImportStats();
            await db.WithConnection(async con => {
                stats.Created += await con.ExecuteAsync(@"
                    INSERT
                    INTO
                        setlist_songs_plays

                        (
                            played_setlist_song_id,
                            played_setlist_show_id
                        )
                    VALUES
                        (
                            @songId,
                            @showId
                        )
                    ON CONFLICT
                        ON CONSTRAINT setlist_songs_plays_song_id_show_id_key
                        DO NOTHING
                ", songs.Select(song => new { showId = show.id, songId = song.id }));

                stats.Removed += await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        setlist_songs_plays
                    WHERE
                        played_setlist_show_id = @showId
                        AND NOT(played_setlist_song_id = ANY(@songIds))
                ", new { showId = show.id, songIds = songs.Select(s => s.id).ToList() });
            });

            return stats;
        }
    }
}