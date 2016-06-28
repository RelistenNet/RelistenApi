using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class VenueService : RelistenDataServiceBase
    {
        public VenueService(DbService db) : base(db) { }

        public async Task<Venue> ForGlobalUpstreamIdentifier(string upstreamId)
        {
            return await ForUpstreamIdentifier(null, upstreamId);
        }

        public async Task<IEnumerable<Venue>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Venue>(@"
                SELECT
                    v.*, COUNT(s.id) as shows_at_venue
                FROM
                    venues v
                    JOIN setlist_shows s ON s.venue_id = v.id
                WHERE
                    s.artist_id = 1
                    OR v.artist_id = 1
                GROUP BY
                	v.id
            ", artist));
        }

        public async Task<Venue> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            if (artist != null)
            {
                return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Venue>(@"
                    SELECT
                        *
                    FROM
                        venues
                    WHERE
                        artist_id = @artistId
                        AND upstream_identifier = @upstreamId
                ", new { artistId = artist.id, upstreamId }));
            }
            else
            {
                return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Venue>(@"
                    SELECT
                        *
                    FROM
                        venues
                    WHERE
                        upstream_identifier = @upstreamId
                ", new { upstreamId }));
            }
        }

        public async Task<Venue> Save(Venue venue)
        {
            if (venue.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Venue>(@"
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
                ", venue));
            }
            else
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Venue>(@"
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
                ", venue));
            }
        }

    }


}