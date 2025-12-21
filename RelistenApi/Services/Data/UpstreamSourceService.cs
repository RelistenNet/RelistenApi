using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class UpstreamSourceService : RelistenDataServiceBase, IUpstreamSourceLookup
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

        public async Task<UpstreamSource?> FindUpstreamSourceByName(string name)
        {
            return await db.WithConnection(conn => conn.QuerySingleOrDefaultAsync<UpstreamSource>(@"
                SELECT
                    *
                FROM
                    upstream_sources
                WHERE
                    name = @name
            ", new {name}));
        }

        public async Task ReplaceUpstreamSourcesForArtist(SlimArtist artist,
            IEnumerable<SlimArtistUpstreamSource> sources)
        {
            await db.WithWriteConnection(async conn =>
            {
                await conn.ExecuteAsync(@"
                    DELETE FROM
                        artists_upstream_sources
                    WHERE
                        artist_id = @id
                ", new {artist.id});

                var artistSources = sources.Select(s => new ArtistUpstreamSource
                {
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

        public async Task EnsureUpstreamSourceForArtist(int artistId, int upstreamSourceId, string upstreamIdentifier)
        {
            await db.WithWriteConnection(conn => conn.ExecuteAsync(@"
                INSERT INTO
                    artists_upstream_sources
                    (upstream_source_id, artist_id, upstream_identifier)
                SELECT
                    @upstreamSourceId, @artistId, @upstreamIdentifier
                WHERE NOT EXISTS (
                    SELECT
                        1
                    FROM
                        artists_upstream_sources
                    WHERE
                        upstream_source_id = @upstreamSourceId
                        AND artist_id = @artistId
                )
            ", new {artistId, upstreamSourceId, upstreamIdentifier}));
        }
    }
}
