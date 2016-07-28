using System.Data;
using Relisten.Api.Models;
using Dapper;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Data
{
    public class SourceService : RelistenDataServiceBase
    {
        public SourceService(DbService db) : base(db) { }

        public async Task<Source> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.WithConnection(con => con.QueryFirstOrDefaultAsync<Source>(@"
                SELECT
                    *
                FROM
                    sources
                WHERE
                    artist_id = @artistId
                    AND upstream_identifier = @upstreamId
            ", new { artistId = artist.id, upstreamId }));
        }

        public async Task<IEnumerable<Source>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con => con.QueryAsync<Source>(@"
                SELECT
                    *
                FROM
                    sources
                WHERE
                    artist_id = @id
            ", artist));
        }

        public async Task<int> DropAllSetsAndTracksForSource(Source source)
        {
            return await db.WithConnection(async con => {
                var cnt = await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        source_sets
                    WHERE
                        source_id = @id
                ", source);

                cnt += await con.ExecuteAsync(@"
                    DELETE
                    FROM
                        source_tracks
                    WHERE
                        source_id = @id
                ", source);

                return cnt;
            });
        } 

        public async Task<Source> Save(Source source)
        {
            if (source.id != 0)
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Source>(@"
                    UPDATE
                        sources
                    SET
                        artist_id = @artist_id,
                        show_id = @show_id,
                        is_soundboard = @is_soundboard,
                        is_remaster = @is_remaster,
                        avg_rating = @avg_rating,
                        num_reviews = @num_reviews,
                        upstream_identifier = @upstream_identifier,
                        has_jamcharts = @has_jamcharts,
                        description = @description,
                        taper_notes = @taper_notes,
                        source = @source,
                        taper = @taper,
                        transferrer = @transferrer,
                        lineage = @lineage,
                        updated_at = @updated_at,
                        display_date = @display_date,
                        venue_id = @venue_id
                    WHERE
                        id = @id
                    RETURNING *
                ", source));
            }
            else
            {
                return await db.WithConnection(con => con.QuerySingleAsync<Source>(@"
                    INSERT INTO
                        sources

                        (
                            artist_id,
                            show_id,
                            is_soundboard,
                            is_remaster,
                            avg_rating,
                            num_reviews,
                            upstream_identifier,
                            has_jamcharts,
                            description,
                            taper_notes,
                            source,
                            taper,
                            transferrer,
                            lineage,
                            updated_at,
                            display_date,
                            venue_id
                        )
                    VALUES
                        (
                            @artist_id,
                            @show_id,
                            @is_soundboard,
                            @is_remaster,
                            @avg_rating,
                            @num_reviews,
                            @upstream_identifier,
                            @has_jamcharts,
                            @description,
                            @taper_notes,
                            @source,
                            @taper,
                            @transferrer,
                            @lineage,
                            @updated_at,
                            @display_date,
                            @venue_id
                        )
                    RETURNING *
                ", source));
            }
        }
    }
}