using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class LinkService : RelistenDataServiceBase
    {
        public LinkService(DbService db) : base(db)
        {
        }

        public async Task<IEnumerable<Link>> AddLinksForSource(Source src, IEnumerable<Link> links)
        {
            var linkList = links.ToList();
            if (linkList.Count == 0)
            {
                return Enumerable.Empty<Link>();
            }

            return await db.WithWriteConnection(async con =>
            {
                // Batch insert using UNNEST for all links at once
                var inserted = await con.QueryAsync<Link>(@"
                    INSERT INTO
                        links
                        (
                            source_id,
                            upstream_source_id,
                            for_reviews,
                            for_ratings,
                            for_source,
                            url,
                            label
                        )
                    SELECT
                        source_id,
                        upstream_source_id,
                        for_reviews,
                        for_ratings,
                        for_source,
                        url,
                        label
                    FROM UNNEST(
                        @source_ids::int[],
                        @upstream_source_ids::bigint[],
                        @for_reviews::boolean[],
                        @for_ratings::boolean[],
                        @for_sources::boolean[],
                        @urls::text[],
                        @labels::text[]
                    ) AS t(source_id, upstream_source_id, for_reviews, for_ratings, for_source, url, label)
                    ON CONFLICT (source_id, upstream_source_id) DO UPDATE SET
                        for_reviews = EXCLUDED.for_reviews,
                        for_ratings = EXCLUDED.for_ratings,
                        for_source = EXCLUDED.for_source,
                        url = EXCLUDED.url,
                        label = EXCLUDED.label
                    RETURNING *
                ", new
                {
                    source_ids = linkList.Select(l => l.source_id).ToArray(),
                    upstream_source_ids = linkList.Select(l => l.upstream_source_id).ToArray(),
                    for_reviews = linkList.Select(l => l.for_reviews).ToArray(),
                    for_ratings = linkList.Select(l => l.for_ratings).ToArray(),
                    for_sources = linkList.Select(l => l.for_source).ToArray(),
                    urls = linkList.Select(l => l.url).ToArray(),
                    labels = linkList.Select(l => l.label).ToArray()
                });

                return inserted;
            });
        }
    }
}
