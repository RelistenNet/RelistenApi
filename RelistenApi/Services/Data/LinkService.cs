﻿using System.Collections.Generic;
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
            return await db.WithWriteConnection(async con =>
            {
                var inserted = new List<Link>();

                foreach (var link in links)
                {
                    var p = new
                    {
                        link.id,
                        link.source_id,
                        link.upstream_source_id,
                        link.for_reviews,
                        link.for_ratings,
                        link.for_source,
                        link.url,
                        link.label
                    };

                    inserted.Add(await con.QuerySingleAsync<Link>(@"
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
	                    VALUES
	                        (
	                            @source_id,
	                            @upstream_source_id,
	                            @for_reviews,
	                            @for_ratings,
	                            @for_source,
	                            @url,
	                            @label
	                        )
						ON CONFLICT (source_id, upstream_source_id) DO UPDATE SET
							for_reviews = EXCLUDED.for_reviews,
							for_ratings = EXCLUDED.for_ratings,
							for_source = EXCLUDED.for_source,
							url = EXCLUDED.url,
							label = EXCLUDED.label

	                    RETURNING *
                    ", p));
                }

                return inserted;
            });
        }
    }
}
