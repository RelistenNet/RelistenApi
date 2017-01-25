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
        public Identifier()
        {
            Id = null;
            Slug = null;
        }
        
        public Identifier(string idAndOrSlug)
        {
            int id = -1;

            var parts = idAndOrSlug.Split(new[] { '-' }, 2);

            if (parts.Length == 1)
            {
                if (int.TryParse(parts[0], out id))
                {
                    this.Id = id;
                    this.Slug = null;
                }
                else
                {
                    this.Id = null;
                    this.Slug = idAndOrSlug;
                }
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
        private ShowService _showService { get; set; }

        public VenueService(DbService db, ShowService showService) : base(db)
        {
            _showService = showService;
        }

        public async Task<Venue> ForGlobalUpstreamIdentifier(string upstreamId)
        {
            return await ForUpstreamIdentifier(null, upstreamId);
        }

        public async Task<IEnumerable<Venue>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Venue>(@"
                SELECT
                    v.*,
                    CASE
                    	WHEN COUNT(DISTINCT src.show_id) = 0 THEN
                    		COUNT(s.id)
                    	ELSE
                    		COUNT(DISTINCT src.show_id)
                    END as shows_at_venue
                FROM
                    venues v
                    LEFT JOIN shows s ON s.venue_id = v.id
                    LEFT JOIN sources src ON src.venue_id = v.id
                WHERE
                    v.artist_id = @id
                GROUP BY
                	s.artist_id, v.id
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

        public async Task<T> ForId<T>(int id) where T : Venue
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<T>(@"
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
        public async Task<VenueWithShowCount> ForId(int id)
        {
            return await ForId<VenueWithShowCount>(id);
        }

        public async Task<VenueWithShows> ForIdWithShows(Artist artist, int id)
        {
            var venue = await ForId<VenueWithShows>(id);

            if (venue == null)
            {
                return null;
            }

            venue.shows = new List<Show>();
            venue.shows.AddRange(await _showService.ShowsForCriteria(artist,
                "s.artist_id = @artist_id AND s.venue_id = @venue_id",
                new { artist_id = artist.id, venue_id = venue.id }
            ));

            return venue;
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
                        past_names = @past_names,
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
                            past_names,
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
                            @past_names,
                            @slug
                        )
                    RETURNING *
                ", venue));
            }
        }

    }


}