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
/// Publishes a complete calculated population in a short transaction. Calculation
/// happens beforehand. The refresh row orders competing publications of that
/// population; it does not lock catalog rows or serialize different artists.
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
                    CachedShowPopularityMapper.GetRankingMomentumScore(item.Value)
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

    private async Task Replace(
        string kind,
        Guid? artist,
        IEnumerable<StoredPopularity> values,
        DateTime generatedAt
    )
    {
        // Names are chosen only by the typed entrypoints above. JSON is a batch
        // parameter, not SQL, and avoids a round trip for every calculated row.
        var rows = JsonConvert.SerializeObject(values.OrderBy(row => row.uuid));
        await using var connection = db.CreateConnection(longTimeout: false, readOnly: false);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var accepted = await connection.ExecuteScalarAsync<bool>(
            """
            INSERT INTO popularity_refreshes(kind,scope_uuid,generated_at)
            VALUES (@kind,@scope,@generatedAt)
            ON CONFLICT(kind,scope_uuid) DO UPDATE SET generated_at=EXCLUDED.generated_at
            WHERE popularity_refreshes.generated_at <= EXCLUDED.generated_at
            RETURNING true
            """,
            new
            {
                kind,
                scope = artist ?? Guid.Empty,
                generatedAt,
            },
            transaction
        );
        if (!accepted)
        {
            return;
        }

        var ownerJoin = kind == "artist" ? "" : " JOIN artists a ON a.id=entity.artist_id";
        var ownerColumns = kind == "artist" ? "" : ",artist_uuid,is_global";
        var ownerValues = kind == "artist" ? "" : ",a.uuid,@isGlobal";
        var ownerFilter = kind == "artist" ? "true" : "(@artist::uuid IS NULL OR a.uuid=@artist)";
        var population =
            kind == "artist"
                ? "true"
                : "is_global=@isGlobal AND (@artist::uuid IS NULL OR artist_uuid=@artist)";
        var conflict = kind == "artist" ? "uuid" : "uuid,is_global";
        var assignments = string.Join(
            ',',
            Columns.Split(',').Select(column => $"{column}=EXCLUDED.{column}")
        );
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
                    plays_6h bigint,plays_90d bigint,trend_ratio double precision,
                    momentum_score double precision,organic_momentum_score double precision)
            )
            INSERT INTO {kind}_popularity(uuid,{Columns}{ownerColumns})
            SELECT x.uuid,{string.Join(
                ',',
                Columns.Split(',').Select(column => "x." + column)
            )}{ownerValues}
            FROM input x JOIN {kind}s entity ON entity.uuid=x.uuid{ownerJoin}
            WHERE {ownerFilter} ORDER BY x.uuid
            ON CONFLICT({conflict}) DO UPDATE SET {assignments};
            DELETE FROM {kind}_popularity WHERE {population}
              AND uuid NOT IN (
                SELECT entity.uuid FROM jsonb_array_elements(@rows::jsonb) x
                JOIN {kind}s entity ON entity.uuid=(x->>'uuid')::uuid{ownerJoin}
                WHERE {ownerFilter});
            """,
            new
            {
                rows,
                artist,
                isGlobal = artist is null,
            },
            transaction
        );
        await transaction.CommitAsync();
    }

    // Previously published artist populations must also refresh after their last
    // play ages out. Otherwise their old scores would survive indefinitely.
    internal async Task<Guid[]> ReadArtistsToRefresh(CancellationToken cancellationToken) =>
        (
            await db.WithWriteConnection(connection =>
                connection.QueryAsync<Guid>(
                    new CommandDefinition(
                        """
                        SELECT uuid FROM artist_popularity
                        UNION SELECT artist_uuid FROM show_popularity WHERE NOT is_global
                        UNION SELECT artist_uuid FROM year_popularity WHERE NOT is_global
                        ORDER BY 1
                        """,
                        cancellationToken: cancellationToken
                    )
                )
            )
        ).ToArray();

    private const string Columns =
        "plays_30d,hours_30d,hot_score_30d,plays_7d,hours_7d,hot_score_7d,plays_48h,hours_48h,hot_score_48h,plays_6h,plays_90d,trend_ratio,momentum_score,organic_momentum_score";
}
