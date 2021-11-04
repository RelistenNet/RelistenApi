using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class EraService : RelistenDataServiceBase
    {
        public EraService(DbService db, ShowService showService) : base(db)
        {
            _showService = showService;
        }

        private ShowService _showService { get; }

        public async Task<Era> ForName(Artist artist, string name)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Era>(@"
                SELECT
                    *
                FROM
                    eras
                WHERE
                    artist_id = @artistId
                    AND name = @name
            ", new {artistId = artist.id, name}));
        }

        public async Task<IEnumerable<Era>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Era>(@"
                SELECT
                    *
                FROM
                    eras
                WHERE
                    artist_id = @id
                ORDER BY
                    ""order"" ASC
            ", new {artist.id}));
        }

        public async Task<Era> Save(Era era)
        {
            var p = new
            {
                era.id,
                era.artist_id,
                era.name,
                era.order,
                era.updated_at
            };

            if (era.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Era>(@"
                    UPDATE
                        eras
                    SET
                        artist_id = @artist_id,
                        name = @name,
                        order = @order,
                        updated_at = @updated_at
                    WHERE
                        id = @id
                    RETURNING *
                ", p));
            }

            return await db.WithConnection(con => con.QuerySingleAsync<Era>(@"
                    INSERT INTO
                        eras

                        (
                            artist_id,
                            name,
                            ""order"",
                            updated_at
                        )
                    VALUES
                        (
                            @artist_id,
                            @name,
                            @order,
                            @updated_at
                        )
                    RETURNING *
                ", p));
        }
    }
}
