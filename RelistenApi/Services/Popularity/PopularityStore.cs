using System;
using System.Collections.Generic;
using System.Data;
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
    /// <summary>Reads only selected artist-local show scores in the caller's connection/snapshot.</summary>
    internal static async Task<
        IReadOnlyDictionary<Guid, PopularityMetrics>
    > ReadArtistLocalShowMetrics(
        IDbConnection connection,
        Guid[] ids,
        IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default
    )
    {
        var rows = await connection.QueryAsync<StoredPopularity>(
            new CommandDefinition(
                $"""
                SELECT {StoredPopularity.ReadColumns} FROM show_popularity p
                JOIN shows s ON s.uuid=p.uuid JOIN artists a ON a.id=s.artist_id
                WHERE NOT p.is_global AND p.uuid=ANY(@ids) AND p.artist_uuid=a.uuid
                  AND {StoredPopularity.MetadataFilter}
                """,
                new { ids },
                transaction,
                commandTimeout: 30,
                cancellationToken: cancellationToken
            )
        );
        return rows.ToDictionary(row => row.uuid, row => row.ToMetrics()!);
    }

    public Task ReplaceArtists(
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt
    ) => Replace(new(PopularityPopulationKind.Artist), map, generatedAt);

    public Task ReplaceGlobalShows(
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt
    ) => Replace(new(PopularityPopulationKind.Show), map, generatedAt);

    internal Task ReplaceArtistShows(
        Guid artist,
        IReadOnlyDictionary<Guid, CachedShowPopularity> map,
        DateTime generatedAt
    ) =>
        Replace(
            new(PopularityPopulationKind.Show, artist),
            map.ToDictionary(
                item => item.Key,
                item => CachedShowPopularityMapper.ToPopularityMetrics(item.Value)
            ),
            generatedAt,
            map
        );

    public Task ReplaceYears(
        Guid? artist,
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt
    ) => Replace(new(PopularityPopulationKind.Year, artist), map, generatedAt);

    private async Task Replace(
        PopularityPopulation population,
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        DateTime generatedAt,
        IReadOnlyDictionary<Guid, CachedShowPopularity>? shows = null
    )
    {
        var rows = BuildRows(map, shows).ToDictionary(row => row.uuid);
        await AddYearlyTotals(population, rows, DateOnly.FromDateTime(generatedAt));
        await Publish(population, rows.Values, generatedAt);
    }

    private IEnumerable<StoredPopularity> BuildRows(
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        IReadOnlyDictionary<Guid, CachedShowPopularity>? shows
    )
    {
        // The Redis maps retain their legacy populations. V4 scores compare only
        // 30-day-active targets.
        Dictionary<Guid, double>? activeScores = null;
        if (map.Values.Any(metrics => metrics.windows.days_30d.plays == 0))
        {
            var active = map.Where(item => item.Value.windows.days_30d.plays > 0).ToArray();
            var scores = PopularityService.CalculateMomentumScores(
                active.Select(item => item.Value).ToArray()
            );
            activeScores = active
                .Select((item, index) => (item.Key, Score: scores[index]))
                .ToDictionary(item => item.Key, item => item.Score);
        }

        foreach (var (id, metrics) in map)
        {
            var show = shows?.GetValueOrDefault(id);
            double? organic = show is null
                ? null
                : CachedShowPopularityMapper.GetRankingMomentumScore(show);
            var row = StoredPopularity.From(id, metrics, organic, roundScores: shows is null);

            if (activeScores is not null)
            {
                if (activeScores.TryGetValue(id, out var raw))
                {
                    row.momentum_score = shows is null ? Math.Round(raw, 4) : raw;
                    if (show is not null)
                    {
                        row.organic_momentum_score =
                            ShowMomentumScoring.ComputeOrganicMomentumScore(
                                raw,
                                show.momentum?.otd_penalty_ratio_7d ?? 0
                            );
                    }
                }
                else
                {
                    row.momentum_score = null;
                    row.organic_momentum_score = null;
                    row.trend_ratio = 0;
                }
            }

            yield return row;
        }
    }

    private async Task AddYearlyTotals(
        PopularityPopulation population,
        Dictionary<Guid, StoredPopularity> rows,
        DateOnly through
    )
    {
        // Keep the yearly scan on the same read path as the recent calculation.
        // A failed scan leaves the previous Postgres and Redis publications intact.
        var totals = await db.WithConnection(async connection =>
        {
            // The rollup scans have large planner estimates. On production JIT
            // compilation made one of them take 14.5s instead of 55ms. Scope the
            // setting to this read-only transaction, as CatalogReadSession does.
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                "SET TRANSACTION READ ONLY; SET LOCAL jit = off",
                transaction: transaction
            );
            return await PlayRollupReader.ReadPopularity(
                connection,
                transaction,
                population,
                through
            );
        });
        foreach (var total in totals)
        {
            if (!rows.TryGetValue(total.Id, out var row))
            {
                row = new() { uuid = total.Id };
                rows.Add(total.Id, row);
            }

            row.plays_1y = total.Plays;
            row.hours_1y = Math.Round(total.Seconds / 3600.0, 4);
            row.hot_score_1y = Math.Round(Math.Sqrt(total.Plays), 4);
        }
    }

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
                WHERE (generated_at<=@cutoff OR generated_at IS NULL) AND CASE r.kind
                    WHEN 'artist' THEN EXISTS(SELECT 1 FROM artists)
                    WHEN 'show' THEN CASE WHEN r.artist_uuid IS NULL THEN EXISTS(SELECT 1 FROM shows)
                        ELSE EXISTS(SELECT 1 FROM shows s JOIN artists a ON a.id=s.artist_id WHERE a.uuid=r.artist_uuid) END
                    WHEN 'year' THEN CASE WHEN r.artist_uuid IS NULL THEN EXISTS(SELECT 1 FROM years)
                        ELSE EXISTS(SELECT 1 FROM years y JOIN artists a ON a.id=y.artist_id WHERE a.uuid=r.artist_uuid) END
                    ELSE false END
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

    private async Task Publish(
        PopularityPopulation population,
        IEnumerable<StoredPopularity> values,
        DateTime generatedAt
    )
    {
        // Read models cover every current catalog target. An absent calculated
        // value becomes zero counters and NULL momentum, clearing prior activity
        // without deleting rows. JSON is one batch parameter, not executable SQL.
        var rows = JsonConvert.SerializeObject(values);
        var kind = population.StorageKind;
        var artist = population.Artist;
        await using var connection = db.CreateConnection(longTimeout: false, readOnly: false);
        await connection.OpenAsync();
        var ownerJoin = kind == "artist" ? "" : " JOIN artists a ON a.id=entity.artist_id";
        var ownerColumns = kind == "artist" ? "" : ",artist_uuid,is_global";
        var ownerValues = kind == "artist" ? "" : ",a.uuid,@isGlobal";
        var ownerFilter = kind == "artist" ? "true" : "(@artist::uuid IS NULL OR a.uuid=@artist)";
        var conflict = kind == "artist" ? "uuid" : "uuid,is_global";
        var columns = Columns + ",plays_1y,hours_1y,hot_score_1y";
        if (kind == "show")
        {
            columns += ",organic_momentum_score";
        }

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
                    organic_momentum_score double precision,
                    plays_1y bigint,hours_1y double precision,hot_score_1y double precision)
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
