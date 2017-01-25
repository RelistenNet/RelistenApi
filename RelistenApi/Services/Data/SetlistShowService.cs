using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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

        public async Task<SetlistShow> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<SetlistShow>(@"
                SELECT
                    *
                FROM
                    setlist_shows
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstreamId
            ", new { artistId = artist.id, upstreamId }));
        }

        public async Task<IEnumerable<SetlistShow>> AllForArtist(Artist artist, bool withVenuesToursAndEras = false)
        {
            if (withVenuesToursAndEras)
            {
                return await db.WithConnection(con => con.QueryAsync<SetlistShow, Tour, Venue, Era, SetlistShow>(@"
                    SELECT
                        s.*, t.*, v.*, e.*
                    FROM
                        setlist_shows s
                        LEFT JOIN tours t ON s.tour_id = t.id
                        LEFT JOIN venues v ON s.venue_id = v.id
                        LEFT JOIN eras e ON s.era_id = e.id
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
                }, artist));
            }

            return await db.WithConnection(con => con.QueryAsync<SetlistShow>(@"
                SELECT
                    *
                FROM
                    setlist_shows
                WHERE
                    artist_id = @id
            ", artist));
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
            ", artist));
        }

        public async Task<SetlistShow> Save(SetlistShow show)
        {
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
                        updated_at = @updated_at
                    WHERE
                        id = @id
                    RETURNING *
                ", show));
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
                            updated_at
                        )
                    VALUES
                        (
                            @artist_id,
                            @venue_id,
                            @date,
                            @tour_id,
                            @upstream_identifier,
                            @updated_at
                        )
                    RETURNING *
                ", show));
            }
        }

        public async Task<int> RemoveSongPlays(SetlistShow show)
        {
            return await db.WithConnection(con => con.ExecuteAsync(@"
                DELETE
                FROM
                    setlist_songs_plays
                WHERE
                    played_setlist_show_id = @showId
            ", new { showId = show.id }));
        }

        public async Task<int> AddSongPlays(SetlistShow show, IEnumerable<SetlistSong> songs)
        {
            return await db.WithConnection(con => con.ExecuteAsync(@"
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
            ", songs.Select(song => new { showId = show.id, songId = song.id })));
        }
    }
}