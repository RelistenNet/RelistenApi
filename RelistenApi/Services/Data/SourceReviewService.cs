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
            const string guidSnippet =
                @"md5(@source_id || '::review::' || COALESCE('' || @rating, 'NULL') || COALESCE('' || @title, 'NULL') || COALESCE('' || @author, 'NULL') || @updated_at)::uuid";
            return await db.WithConnection(async con =>
            {
                var inserted = new List<SourceReview>();

                foreach (var review in reviews)
                {
                    var p = new
                    {
                        review.id,
                        review.source_id,
                        review.rating,
                        review.title,
                        review.review,
                        review.author,
                        review.updated_at
                    };

                    inserted.Add(await con.QuerySingleAsync<SourceReview>($@"
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
                    VALUES
                        (
                            @source_id,
                            @rating,
                            @title,
                            COALESCE(@review, ''),
                            @author,
                            @updated_at,
                            {guidSnippet}
                        )
                    ON CONFLICT ON CONSTRAINT source_reviews_uuid
                    DO
                        UPDATE SET
                            review = EXCLUDED.review
                    
                    RETURNING *
                    ", p));
                }

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
