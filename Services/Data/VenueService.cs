using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class Identifier
    {
        public Identifier(string idAndOrSlug)
        {
            int id = -1;

            var parts = idAndOrSlug.Split('_');

            if (parts.Length == 1)
            {
                this.Id = null;
                this.Slug = idAndOrSlug;
            }
            else
            {
                if (int.TryParse(parts[0], out id))
                {
                    this.Id = id;
                    this.Slug = parts[1];
                }
                else
                {
                    // ¯\_(ツ)_/¯
                    this.Id = null;
                    this.Slug = idAndOrSlug;
                }
            }
        }

        public int? Id { get; set; }
        public string Slug { get; set; }
    }

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
                    LEFT JOIN shows s ON s.venue_id = v.id
                WHERE
                    s.artist_id = @id
                    OR v.artist_id = @id
                    OR v.artist_id IS NULL
                GROUP BY
                	s.artist_id, v.id
                HAVING
                	s.artist_id = @id
                    AND COUNT(s.id) > 0
                ORDER BY
                	v.name ASC
            ", artist));
        }
        public async Task<IEnumerable<Venue>> AllValidForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Venue>(@"
                SELECT
                    v.*, COUNT(s.id) as shows_at_venue
                FROM
                    venues v
                    LEFT JOIN shows s ON s.venue_id = v.id
                WHERE
                    s.artist_id = @id
                    OR v.artist_id = @id
                    OR v.artist_id IS NULL
                GROUP BY
                	v.id
                ORDER BY
                	v.name ASC
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

        public async Task<Venue> ForId(int id)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Venue>(@"
                SELECT
                    v.*, COUNT(s.id) as shows_at_venue
                FROM
                    venues v
                    JOIN shows s ON s.venue_id = v.id
                WHERE
                    v.id = @id
                GROUP BY
                	v.id
            ", new { id = id }));
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
                        updated_at = @updated_at,
                        slug = @slug
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
                            updated_at,
                            slug
                        )
                    VALUES
                        (
                            @artist_id,
                            @latitude,
                            @longitude,
                            @name,
                            @location,
                            @upstream_identifier,
                            @updated_at,
                            @slug
                        )
                    RETURNING *
                ", venue));
            }
        }

    }


}