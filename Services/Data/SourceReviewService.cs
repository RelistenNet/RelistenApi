using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class SourceReviewService : RelistenDataServiceBase
    {
        public SourceReviewService(DbService db) : base(db) { }

        public async Task<IEnumerable<SourceReview>> AllForSources(IEnumerable<int> source_ids)
        {
            return await db.WithConnection(con => con.QueryAsync<SourceReview>(@"
                SELECT
                    r.*
                FROM
                    source_reviews r
                WHERE
                    source_id = ANY(@source_ids)
            ", new { source_ids }));
        }

        public async Task<IEnumerable<SourceReview>> InsertAll(Artist artist, IEnumerable<SourceReview> songs)
        {
            return await db.WithConnection(async con =>
            {
                var inserted = await con.ExecuteAsync(@"
                    INSERT INTO
                        setlist_songs

                        (
                            source_id,
                            rating,
                            title,
                            review,
                            author,
                            updated_at
                        )
                    VALUES
                        (
                            @source_id,
                            @rating,
                            @title,
                            @review,
                            @author,
                            @updated_at
                        )
                ", songs);

                return await AllForSources(songs.Select(song => song.source_id).Distinct());
            });
        }
    }
}