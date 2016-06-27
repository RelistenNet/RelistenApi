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
        protected IDbConnection db { get; set; }

        protected RelistenDataServiceBase(DbService db)
        {
            this.db = db.connection;
        }
    }

    public class SetlistShowService : RelistenDataServiceBase
    {
        public SetlistShowService(DbService db) : base(db) { }

        public async Task<SetlistShow> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.QueryFirstOrDefaultAsync<SetlistShow>(@"
                SELECT
                    *
                FROM
                    setlist_shows
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstreamId
            ", new { artistId = artist.id, upstreamId });
        }

        public async Task<SetlistShow> Save(SetlistShow show)
        {
            if (show.id != 0)
            {
                return await db.QuerySingleAsync<SetlistShow>(@"
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
                ", show);
            }
            else
            {
                return await db.QuerySingleAsync<SetlistShow>(@"
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
                ", show);
            }
        }

        public async Task<int> RemoveSongPlays(SetlistShow show)
        {
            return await db.ExecuteAsync(@"
                DELETE
                FROM
                    setlist_songs_plays
                WHERE
                    played_setlist_show_id = @showId
            ", new { showId = show.id });
        }

        public async Task<int> AddSongPlays(SetlistShow show, IEnumerable<SetlistSong> songs)
        {
            return await db.ExecuteAsync(@"
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
            ", songs.Select(song => new { showId = show.id, songId = song.id }));
        }
    }
    public class SetlistSongService : RelistenDataServiceBase
    {
        public SetlistSongService(DbService db) : base(db) { }

        public async Task<IEnumerable<SetlistSong>> ForUpstreamIdentifiers(Artist artist, IEnumerable<string> upstreamIds)
        {
            return await db.QueryAsync<SetlistSong>(@"
                SELECT
                    *
                FROM
                    setlist_songs
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = ANY(@upstreamIds)
            ", new { artistId = artist.id, upstreamIds });
        }

        public async Task<SetlistSong> Save(SetlistSong song)
        {
            if (song.id != 0)
            {
                return await db.QuerySingleAsync<SetlistSong>(@"
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
                ", song);
            }
            else
            {
                return await db.QuerySingleAsync<SetlistSong>(@"
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
                ", song);
            }
        }
    }

    public class VenueService : RelistenDataServiceBase
    {
        public VenueService(DbService db) : base(db) { }

        public async Task<Venue> ForGlobalUpstreamIdentifier(string upstreamId)
        {
            return await ForUpstreamIdentifier(null, upstreamId);
        }

        public async Task<Venue> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            if (artist != null)
            {
                return await db.QueryFirstOrDefaultAsync<Venue>(@"
                    SELECT
                        *
                    FROM
                        venues
                    WHERE
                        artist_id = @artistId
                        AND upstream_identifier = @upstreamId
                ", new { artistId = artist.id, upstreamId });
            }
            else
            {
                return await db.QueryFirstOrDefaultAsync<Venue>(@"
                    SELECT
                        *
                    FROM
                        venues
                    WHERE
                        upstream_identifier = @upstreamId
                ", new { upstreamId });
            }
        }

        public async Task<Venue> Save(Venue venue)
        {
            if (venue.id != 0)
            {
                return await db.QuerySingleAsync<Venue>(@"
                    UPDATE
                        venues
                    SET
                        artist_id = @artist_id,
                        latitude = @latitude,
                        longitude = @longitude,
                        name = @name,
                        location = @location,
                        upstream_identifier = @upstream_identifier,
                        updated_at = @updated_at
                    WHERE
                        id = @id
                    RETURNING *
                ", venue);
            }
            else
            {
                return await db.QuerySingleAsync<Venue>(@"
                    INSERT INTO
                        venues

                        (
                            artist_id,
                            latitude,
                            longitude,
                            name,
                            location,
                            upstream_identifier,
                            updated_at
                        )
                    VALUES
                        (
                            @artist_id,
                            @latitude,
                            @longitude,
                            @name,
                            @location,
                            @upstream_identifier,
                            @updated_at
                        )
                    RETURNING *
                ", venue);
            }
        }

    }

    public class TourService : RelistenDataServiceBase
    {
        public TourService(DbService db) : base(db) { }

        public async Task<Tour> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.QueryFirstOrDefaultAsync<Tour>(@"
                SELECT
                    *
                FROM
                    tours
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstreamId
            ", new { artistId = artist.id, upstreamId });
        }

        public async Task<Tour> Save(Tour tour)
        {
            if (tour.id != 0)
            {
                return await db.QuerySingleAsync<Tour>(@"
                    UPDATE
                        tours
                    SET
                        artist_id = @artist_id,
                        start_date = @start_date,
                        end_date = @end_date,
                        name = @name,
                        slug = @slug,
                        upstream_identifier = @upstream_identifier,
                        updated_at = @updated_at
                    WHERE
                        id = @id
                    RETURNING *
                ", tour);
            }
            else
            {
                return await db.QuerySingleAsync<Tour>(@"
                    INSERT INTO
                        tours

                        (
                            artist_id,
                            start_date,
                            end_date,
                            name,
                            slug,
                            upstream_identifier,
                            updated_at
                        )
                    VALUES
                        (
                            @artist_id,
                            @start_date,
                            @end_date,
                            @name,
                            @slug,
                            @upstream_identifier,
                            @updated_at
                        )
                    RETURNING *
                ", tour);
            }
        }
    }
}