using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class TourService : RelistenDataServiceBase
    {
        private ShowService _showService { get; set; }

        public TourService(DbService db, ShowService showService) : base(db)
        {
            _showService = showService;
        }

        public async Task<Tour> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Tour>(@"
                SELECT
                    *
                FROM
                    tours
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstreamId
            ", new { artistId = artist.id, upstreamId }));
        }

        public async Task<IEnumerable<Tour>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Tour>(@"
                SELECT
                    *
                FROM
                    tours
                WHERE
                    artist_id = @id
            ", new { artist.id }));
        }

        public async Task<TourWithShows> ForIdWithShows(Artist artist, int id)
        {
            var tour = await db.WithConnection(con => con.QuerySingleAsync<TourWithShows>(@"
                SELECT
                    *
                FROM
                    tours
                WHERE
                    id = @id
            ", new { id }));

            if (tour == null)
            {
                return null;
            }

            tour.shows = await _showService.ShowsForCriteria(artist,
                "s.artist_id = @artistId AND s.tour_id = @tourId",
                new { artistId = artist.id, tourId = id }
            );

            return tour;
        }

        public async Task<IEnumerable<TourWithShowCount>> AllForArtistWithShowCount(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<TourWithShowCount>(@"
                    SELECT
                        t.*, COUNT(s.id) as shows_on_tour
                    FROM
                        tours t
                        LEFT JOIN setlist_shows s ON s.tour_id = t.id
                    WHERE
                        t.artist_id = @id
                    GROUP BY
                    	t.id
                    ORDER BY t.start_date
            ", new { artist.id }));
        }

        public async Task<Tour> Save(Tour tour)
        {
            var p = new {
                tour.id,
                tour.artist_id,
                tour.start_date,
                tour.end_date,
                tour.name,
                tour.slug,
                tour.upstream_identifier,
                tour.updated_at,
            };

            if (tour.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Tour>(@"
                    UPDATE
                        tours
                    SET
                        artist_id = @artist_id,
                        start_date = @start_date,
                        end_date = @end_date,
                        name = @name,
                        slug = @slug,
                        upstream_identifier = @upstream_identifier,
                        updated_at = @updated_at,
                        uuid = md5(@artist_id || '::tour::' || @upstream_identifier)::uuid
                    WHERE
                        id = @id
                    RETURNING *
                ", p));
            }
            else
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Tour>(@"
                    INSERT INTO
                        tours

                        (
                            artist_id,
                            start_date,
                            end_date,
                            name,
                            slug,
                            upstream_identifier,
                            updated_at,
                            uuid
                        )
                    VALUES
                        (
                            @artist_id,
                            @start_date,
                            @end_date,
                            @name,
                            @slug,
                            @upstream_identifier,
                            @updated_at,
                            md5(@artist_id || '::tour::' || @upstream_identifier)::uuid
                        )
                    RETURNING *
                ", p));
            }
        }
    }
}