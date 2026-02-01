using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

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
            ", new {source_ids}));
        }

        public async Task<IEnumerable<SourceReview>> UpdateAll(Source source, IEnumerable<SourceReview> reviews)
        {
            var reviewList = reviews.ToList();
            if (reviewList.Count == 0)
            {
                return Enumerable.Empty<SourceReview>();
            }

            return await db.WithWriteConnection(async con =>
            {
                // Batch insert using UNNEST for all reviews at once
                var inserted = (await con.QueryAsync<SourceReview>(@"
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
                    SELECT
                        source_id,
                        rating,
                        title,
                        COALESCE(review, ''),
                        author,
                        updated_at,
                        md5(source_id || '::review::' || COALESCE('' || rating, 'NULL') || COALESCE('' || title, 'NULL') || COALESCE('' || author, 'NULL') || updated_at)::uuid
                    FROM UNNEST(
                        @source_ids::int[],
                        @ratings::decimal[],
                        @titles::text[],
                        @reviews::text[],
                        @authors::text[],
                        @updated_ats::timestamp with time zone[]
                    ) AS t(source_id, rating, title, review, author, updated_at)
                    ON CONFLICT ON CONSTRAINT source_reviews_uuid
                    DO
                        UPDATE SET
                            review = EXCLUDED.review
                    RETURNING *
                ", new
                {
                    source_ids = reviewList.Select(r => r.source_id).ToArray(),
                    ratings = reviewList.Select(r => r.rating).ToArray(),
                    titles = reviewList.Select(r => r.title).ToArray(),
                    reviews = reviewList.Select(r => r.review).ToArray(),
                    authors = reviewList.Select(r => r.author).ToArray(),
                    updated_ats = reviewList.Select(r => r.updated_at).ToArray()
                })).ToList();

                await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        source_reviews
                    WHERE
                        source_id = @sourceId
                        AND NOT(uuid = ANY(@reviewGuids))
                ", new {sourceId = source.id, reviewGuids = inserted.Select(i => i.uuid).ToList()});

                return inserted;
            });
        }

        private Guid GuidForReview(SourceReview review)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.Default.GetBytes(
                    $"{review.source_id}::review::{review.rating?.ToString() ?? "NULL"}{review.title ?? "NULL"}{review.author ?? "NULL"}"));
                return new Guid(hash);
            }
        }
    }
}
