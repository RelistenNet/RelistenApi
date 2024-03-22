using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class Identifier
    {
        public Identifier()
        {
            Id = null;
            Slug = null;
            Guid = null;
        }

        public Identifier(string idAndOrSlug)
        {
            var id = -1;

            if (idAndOrSlug.Length == 36 && System.Guid.TryParse(idAndOrSlug, out var guid))
            {
                Guid = guid;
                Id = null;
                Slug = null;
                return;
            }

            var parts = idAndOrSlug.Split(new[] {'-'}, 2);

            if (parts.Length == 1)
            {
                if (int.TryParse(parts[0], out id))
                {
                    Id = id;
                    Slug = null;
                    Guid = null;
                }
                else
                {
                    Id = null;
                    Slug = idAndOrSlug;
                    Guid = null;
                }
            }
            else
            {
                if (int.TryParse(parts[0], out id))
                {
                    Id = id;
                    Slug = parts[1];
                    Guid = null;
                }
                else
                {
                    // ¯\_(ツ)_/¯
                    Id = null;
                    Slug = idAndOrSlug;
                    Guid = null;
                }
            }
        }

        public int? Id { get; set; }
        public string Slug { get; set; }
        public Guid? Guid { get; set; }
    }

    public class VenueService : RelistenDataServiceBase
    {
        public VenueService(DbService db, ShowService showService) : base(db)
        {
            _showService = showService;
        }

        private ShowService _showService { get; }

        public async Task<Venue> ForGlobalUpstreamIdentifier(string upstreamId)
        {
            return await ForUpstreamIdentifier(null, upstreamId);
        }

        public async Task<IEnumerable<VenueWithShowCount>> AllIncludingUnusedForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<VenueWithShowCount>(@"
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
                    LEFT JOIN shows s ON v.id = s.venue_id
                    LEFT JOIN sources src ON src.venue_id = v.id
                WHERE
                    v.artist_id = @id
                GROUP BY
                	v.artist_id, v.id
                ORDER BY
                	v.name ASC
            ", new {artist.id}));
        }

        public async Task<IEnumerable<VenueWithShowCount>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<VenueWithShowCount>(@"
                SELECT
                    v.*,
                    a.uuid as artist_uuid,
                    src.shows_at_venue
                FROM
                	venues v
                	JOIN artists a ON v.artist_id = a.id
                    LEFT JOIN (
                        SELECT
                            src.venue_id
                            , CASE
                    	          WHEN COUNT(DISTINCT src.show_id) = 0 THEN
                    		          COUNT(s.id)
                    	          ELSE
                    		          COUNT(DISTINCT src.show_id)
                              END as shows_at_venue
                        FROM
                            sources src
                            JOIN shows s ON s.id = src.show_id
                        GROUP BY
                            src.venue_id
                    ) src ON src.venue_id = v.id
                WHERE
                    v.artist_id = @id
                ORDER BY
                	v.name ASC
            ", new {artist.id}));
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
                ", new {artistId = artist.id, upstreamId}));
            }

            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Venue>(@"
                    SELECT
                        *
                    FROM
                        venues
                    WHERE
                        upstream_identifier = @upstreamId
                ", new {upstreamId}));
        }

        public async Task<T> ForId<T>(int? id = null, Guid? uuid = null) where T : Venue
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<T>(@"
                SELECT
                    v.*
                    , a.uuid as artist_uuid
                    , CASE
                    	WHEN COUNT(DISTINCT src.show_id) = 0 THEN
                    		COUNT(s.id)
                    	ELSE
                    		COUNT(DISTINCT src.show_id)
                    END as shows_at_venue
                FROM
                	shows s
                    JOIN venues v ON v.id = s.venue_id
                    LEFT JOIN sources src ON src.venue_id = v.id
                    JOIN artists a ON a.id = v.artist_id
                WHERE
                    (v.id = @id OR v.uuid = @uuid)
                GROUP BY
                	v.id, a.uuid
            ", new {id, uuid}));
        }

        public async Task<VenueWithShowCount> ForId(int id)
        {
            return await ForId<VenueWithShowCount>(id);
        }

        public async Task<VenueWithShows> ForIdWithShows(Artist artist, int? id, Guid? uuid = null)
        {
            var venue = await ForId<VenueWithShows>(id, uuid);

            if (venue == null)
            {
                return null;
            }

            var show_ids_sql = @"
                SELECT
                    DISTINCT s.show_id
                FROM
                    sources s
                    JOIN venues v ON v.id = s.venue_id
                WHERE
                    s.artist_id = @artist_id
                    AND (s.venue_id = @venue_id or v.uuid = @venue_uuid)
            ";

            venue.shows = new List<Show>();
            venue.shows.AddRange(await _showService.ShowsForCriteria(artist,
                $"s.artist_id = @artist_id AND s.id IN ({show_ids_sql})",
                new {artist_id = artist.id, venue_id = venue.id, venue_uuid = venue.uuid}
            ));

            return venue;
        }

        public async Task<Venue> Save(Venue venue)
        {
            var p = new
            {
                venue.id,
                venue.artist_id,
                venue.latitude,
                venue.longitude,
                venue.name,
                venue.location,
                venue.upstream_identifier,
                venue.updated_at,
                venue.past_names,
                venue.slug
            };

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
                        slug = @slug,
                        uuid = md5(@artist_id || '::venue::' || @upstream_identifier)::uuid
                    WHERE
                        id = @id
                    RETURNING *
                ", p));
            }

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
                            slug,
                            uuid
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
                            @slug,
                            md5(@artist_id || '::venue::' || @upstream_identifier)::uuid
                        )
                    RETURNING *
                ", p));
        }
    }
}
