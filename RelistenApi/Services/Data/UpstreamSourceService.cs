using System;
using System.Collections.Generic;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Linq;

namespace Relisten.Data
{
	public class UpstreamSourceService : RelistenDataServiceBase
	{
		public UpstreamSourceService(DbService db) : base(db)
		{
		}

		public async Task<IEnumerable<UpstreamSource>> AllUpstreamSources()
		{
			return await db.WithConnection(conn => conn.QueryAsync<UpstreamSource>(@"
                SELECT
                    *
                FROM
                    upstream_sources
            "));
		}

        public async Task ReplaceUpstreamSourcesForArtist(SlimArtist artist, IEnumerable<SlimArtistUpstreamSource> sources)
		{
            await db.WithConnection(async conn => {
                await conn.ExecuteAsync(@"
                    DELETE FROM
                        artists_upstream_sources
                    WHERE
                        artist_id = @id
                ", new { artist.id });

                var artistSources = sources.Select(s => new ArtistUpstreamSource {
                    artist_id = artist.id,
                    upstream_identifier = s.upstream_identifier,
                    upstream_source_id = s.upstream_source_id
                });

                await conn.ExecuteAsync(@"
                    INSERT INTO
                        artists_upstream_sources

                        (upstream_source_id, artist_id, upstream_identifier)
                    VALUES
                        (@upstream_source_id, @artist_id, @upstream_identifier)
                ", artistSources);
            });
		}
	}
}
