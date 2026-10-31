using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

/// <summary>
/// Publishes a complete calculated population in one atomic statement, including
/// inactive targets. Per-row timestamps reject late older calculations. Sorted
/// upserts keep overlapping publications in the same row-lock order.
/// </summary>
public sealed class PopularityStore(DbService db)
{
    public Task ReplaceArtists(
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt
    ) =>
        Replace(
            "artist",
            null,
            map.Select(item => StoredPopularity.From(item.Key, item.Value)),
            generatedAt
        );

    public Task ReplaceGlobalShows(
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt
    ) =>
        Replace(
            "show",
            null,
            map.Select(item => StoredPopularity.From(item.Key, item.Value)),
            generatedAt
        );

    internal Task ReplaceArtistShows(
        Guid artist,
        IReadOnlyDictionary<Guid, CachedShowPopularity> map,
        DateTime generatedAt
    ) =>
        Replace(
            "show",
            artist,
            map.Select(item =>
                StoredPopularity.From(
                    item.Key,
                    CachedShowPopularityMapper.ToPopularityMetrics(item.Value),
                    CachedShowPopularityMapper.GetRankingMomentumScore(item.Value),
                    roundScores: false
                )
            ),
            generatedAt
        );

    public Task ReplaceYears(
        Guid? artist,
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt
    ) =>
        Replace(
            "year",
            artist,
            map.Select(item => StoredPopularity.From(item.Key, item.Value)),
            generatedAt
        );

    internal async Task<PopularityPopulation[]> ReadStale(
        IReadOnlyList<PopularityPopulation> populations,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateConnection(longTimeout: false, readOnly: false);
        await connection.OpenAsync(cancellationToken);
        // A publication stamps every current target in its population atomically,
        // including inactive rows. One representative row therefore supplies age.
        // No catalog targets means there is nothing to calculate or queue.
        var stale = await connection.QueryAsync<long>(
            new CommandDefinition(
                """
                WITH requested AS (
                    SELECT * FROM unnest(@kinds::text[],@artists::uuid[])
                        WITH ORDINALITY r(kind,artist_uuid,ordinal)
                ), freshness AS (
                    SELECT r.*, CASE r.kind
                        WHEN 'artist' THEN (SELECT generated_at FROM artist_popularity LIMIT 1)
                        WHEN 'show' THEN CASE WHEN r.artist_uuid IS NULL
                            THEN (SELECT generated_at FROM show_popularity WHERE is_global LIMIT 1)
                            ELSE (SELECT generated_at FROM show_popularity WHERE NOT is_global AND artist_uuid=r.artist_uuid LIMIT 1) END
                        WHEN 'year' THEN CASE WHEN r.artist_uuid IS NULL
                            THEN (SELECT generated_at FROM year_popularity WHERE is_global LIMIT 1)
                            ELSE (SELECT generated_at FROM year_popularity WHERE NOT is_global AND artist_uuid=r.artist_uuid LIMIT 1) END
                        END generated_at
                    FROM requested r
                )
                SELECT ordinal FROM freshness r
                WHERE generated_at<=@cutoff OR (generated_at IS NULL AND CASE r.kind
                    WHEN 'artist' THEN EXISTS(SELECT 1 FROM artists)
                    WHEN 'show' THEN CASE WHEN r.artist_uuid IS NULL THEN EXISTS(SELECT 1 FROM shows)
                        ELSE EXISTS(SELECT 1 FROM shows s JOIN artists a ON a.id=s.artist_id WHERE a.uuid=r.artist_uuid) END
                    WHEN 'year' THEN CASE WHEN r.artist_uuid IS NULL THEN EXISTS(SELECT 1 FROM years)
                        ELSE EXISTS(SELECT 1 FROM years y JOIN artists a ON a.id=y.artist_id WHERE a.uuid=r.artist_uuid) END
                    ELSE false END)
                """,
                new
                {
                    kinds = populations.Select(population => population.StorageKind).ToArray(),
                    artists = populations.Select(population => population.Artist).ToArray(),
                    cutoff = DateTime.UtcNow.AddSeconds(
                        -PopularityService.DefaultStaleAfterSeconds
                    ),
                },
                commandTimeout: 30,
                cancellationToken: cancellationToken
            )
        );
        var ordinals = stale.ToHashSet();
        return populations.Where((_, index) => ordinals.Contains(index + 1)).ToArray();
    }

    private async Task Replace(
        string kind,
        Guid? artist,
        IEnumerable<StoredPopularity> values,
        DateTime generatedAt
    )
    {
        // Read models cover every current catalog target. An absent calculated
        // value becomes zero counters and NULL momentum, clearing prior activity
        // without deleting rows. JSON is one batch parameter, not executable SQL.
        var rows = JsonConvert.SerializeObject(values);
        await using var connection = db.CreateConnection(longTimeout: false, readOnly: false);
        await connection.OpenAsync();
        var ownerJoin = kind == "artist" ? "" : " JOIN artists a ON a.id=entity.artist_id";
        var ownerColumns = kind == "artist" ? "" : ",artist_uuid,is_global";
        var ownerValues = kind == "artist" ? "" : ",a.uuid,@isGlobal";
        var ownerFilter = kind == "artist" ? "true" : "(@artist::uuid IS NULL OR a.uuid=@artist)";
        var conflict = kind == "artist" ? "uuid" : "uuid,is_global";
        var columns = kind == "show" ? Columns + ",organic_momentum_score" : Columns;
        var fields = columns.Split(',');
        var selected = string.Join(
            ',',
            fields.Select(column =>
                column is "momentum_score" or "organic_momentum_score"
                    ? "x." + column
                    : $"COALESCE(x.{column},0)"
            )
        );
        var assignments =
            string.Join(',', fields.Select(column => $"{column}=EXCLUDED.{column}"))
            + ",generated_at=EXCLUDED.generated_at";
        if (kind != "artist")
        {
            assignments += ",artist_uuid=EXCLUDED.artist_uuid";
        }
        await connection.ExecuteAsync(
            $"""
            WITH input AS (
                SELECT * FROM jsonb_to_recordset(@rows::jsonb) AS x(
                    uuid uuid,plays_30d bigint,hours_30d double precision,hot_score_30d double precision,
                    plays_7d bigint,hours_7d double precision,hot_score_7d double precision,
                    plays_48h bigint,hours_48h double precision,hot_score_48h double precision,
                    trend_ratio double precision,momentum_score double precision,
                    organic_momentum_score double precision)
            )
            INSERT INTO {kind}_popularity(uuid,{columns}{ownerColumns},generated_at)
            SELECT entity.uuid,{selected}{ownerValues},@generatedAt
            FROM {kind}s entity{ownerJoin} LEFT JOIN input x ON x.uuid=entity.uuid
            WHERE {ownerFilter} ORDER BY entity.uuid
            ON CONFLICT({conflict}) DO UPDATE SET {assignments}
            WHERE {kind}_popularity.generated_at<=EXCLUDED.generated_at;
            """,
            new
            {
                rows,
                artist,
                isGlobal = artist is null,
                generatedAt,
            }
        );
    }

    private const string Columns =
        "plays_30d,hours_30d,hot_score_30d,plays_7d,hours_7d,hot_score_7d,plays_48h,hours_48h,hot_score_48h,trend_ratio,momentum_score";
}
