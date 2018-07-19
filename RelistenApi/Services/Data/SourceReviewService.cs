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

        public async Task<int> RemoveAllForSource(Source source)
        {
            return await db.WithConnection(con => con.ExecuteAsync(@"
                DELETE
                FROM
                    source_reviews r
                WHERE
                    source_id = @id
            ", new { source.id }));
        }

        public async Task<IEnumerable<SourceReview>> InsertAll(IEnumerable<SourceReview> reviews)
        {
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SourceReview>();

                foreach (var review in reviews)
                {
                    var p = new {
                        review.id,
                        review.source_id,
                        review.rating,
                        review.title,
                        review.review,
                        review.author,
                        review.updated_at,
                    };

                    inserted.Add(await con.QuerySingleAsync<SourceReview>(@"
                    INSERT INTO
                        source_reviews

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
                            COALESCE(@review, ''),
                            @author,
                            @updated_at
                        )
                        RETURNING *
                    ", p));
                }

                return inserted;
            });
        }
    }
}