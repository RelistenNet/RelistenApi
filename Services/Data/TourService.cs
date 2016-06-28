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
        public TourService(DbService db) : base(db) { }

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
            ", artist));
        }

        public async Task<Tour> Save(Tour tour)
        {
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
                        updated_at = @updated_at
                    WHERE
                        id = @id
                    RETURNING *
                ", tour));
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
                ", tour));
            }
        }
    }
}