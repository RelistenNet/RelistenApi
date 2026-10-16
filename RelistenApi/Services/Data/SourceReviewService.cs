using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class SourceReviewService : RelistenDataServiceBase
    {
        public SourceReviewService(DbService db)
            : base(db) { }

        public async Task<IEnumerable<SourceReview>> AllForSources(IEnumerable<int> source_ids)
        {
            return await db.WithConnection(con =>
                con.QueryAsync<SourceReview>(
                    @"
                SELECT
                    r.*
                FROM
                    source_reviews r
                WHERE
                    source_id = ANY(@source_ids)
            ",
                    new { source_ids }
                )
            );
        }

        /// <summary>
        /// Upserts a source's review snapshot and removes reviews absent from a nonempty snapshot.
        /// Repeated identities use the last supplied review body and retain their existing UUID.
        /// </summary>
        public async Task<IEnumerable<SourceReview>> UpdateAll(
            Source source,
            IEnumerable<SourceReview> reviews
        )
        {
            var reviewList = reviews.ToList();
            if (reviewList.Count == 0)
            {
                return Enumerable.Empty<SourceReview>();
            }

            return await db.WithWriteConnection(async con =>
            {
                // Upstream responses can repeat a review identity, including with a revised
                // body. Deduplicate the actual database UUID before the upsert: PostgreSQL
                // cannot update the same conflict row twice within one INSERT.
                var inserted = (
                    await con.QueryAsync<SourceReview>(
                        @"
                    WITH incoming AS (
                        SELECT *,
                            md5(
                                source_id || '::review::'
                                || COALESCE('' || rating, 'NULL')
                                || COALESCE('' || title, 'NULL')
                                || COALESCE('' || author, 'NULL')
                                || updated_at
                            )::uuid AS uuid
                        FROM UNNEST(
                            @source_ids::int[],
                            @ratings::decimal[],
                            @titles::text[],
                            @reviews::text[],
                            @authors::text[],
                            @updated_ats::timestamp with time zone[]
                        ) WITH ORDINALITY AS t(
                            source_id, rating, title, review, author, updated_at, position
                        )
                    )
                    INSERT INTO
                        source_reviews
                        (
                            source_id,
                            rating,
                            title,
                            review,
                            author,
                            updated_at,
                            uuid
                        )
                    SELECT DISTINCT ON (uuid)
                        source_id,
                        rating,
                        title,
                        COALESCE(review, ''),
                        author,
                        updated_at,
                        uuid
                    FROM incoming
                    ORDER BY uuid, position DESC
                    ON CONFLICT ON CONSTRAINT source_reviews_uuid
                    DO
                        UPDATE SET
                            review = EXCLUDED.review
                    RETURNING *
                ",
                        new
                        {
                            source_ids = reviewList.Select(r => r.source_id).ToArray(),
                            ratings = reviewList.Select(r => r.rating).ToArray(),
                            titles = reviewList.Select(r => r.title).ToArray(),
                            reviews = reviewList.Select(r => r.review).ToArray(),
                            authors = reviewList.Select(r => r.author).ToArray(),
                            updated_ats = reviewList.Select(r => r.updated_at).ToArray(),
                        }
                    )
                ).ToList();

                await con.ExecuteAsync(
                    @"
                    DELETE
                    FROM
                        source_reviews
                    WHERE
                        source_id = @sourceId
                        AND NOT(uuid = ANY(@reviewGuids))
                ",
                    new
                    {
                        sourceId = source.id,
                        reviewGuids = inserted.Select(i => i.uuid).ToList(),
                    }
                );

                return inserted;
            });
        }
    }
}
