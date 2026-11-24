using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Relisten.Api.Models;

namespace Relisten.Services.Popularity;

public sealed partial class PopularityStore
{
    internal async Task PublishDetails(
        Guid artist,
        PerformancePopularity[] performances,
        RecordingPopularity[] recordings,
        SongPopularity[] songs,
        DateTime generatedAt,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The artist checkpoint advances even when every metric row is unchanged,
        // so an older calculation cannot subsequently overwrite or reinsert one.
        var claimed = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO popularity_detail_publications(artist_uuid,generated_at)
                SELECT uuid,@generatedAt FROM artists WHERE uuid=@artist
                ON CONFLICT(artist_uuid) DO UPDATE SET generated_at=EXCLUDED.generated_at
                WHERE popularity_detail_publications.generated_at<EXCLUDED.generated_at
                """,
                new { artist, generatedAt },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        if (claimed == 0)
        {
            return;
        }

        await PublishPopulation(
            transaction,
            "song_performance_popularity",
            ["uuid", "song_id"],
            "shows",
            artist,
            performances.Select(row => DetailFields(row.Metrics, row.Show, row.Song)),
            generatedAt,
            cancellationToken
        );
        await PublishPopulation(
            transaction,
            "recording_popularity",
            ["uuid"],
            "sources",
            artist,
            recordings.Select(row => DetailFields(row.Metrics, row.Source, null)),
            generatedAt,
            cancellationToken
        );
        await PublishPopulation(
            transaction,
            "song_popularity",
            ["song_id"],
            null,
            artist,
            songs.Select(row => DetailFields(row.Metrics, null, row.Song)),
            generatedAt,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
    }

    private static JObject DetailFields(StoredPopularity metrics, Guid? uuid, string? song)
    {
        var fields = JObject.FromObject(metrics);
        if (uuid.HasValue)
        {
            fields["uuid"] = uuid.Value;
        }
        else
        {
            fields.Remove("uuid");
        }

        if (song != null)
        {
            fields["song_id"] = song;
        }
        return fields;
    }

    private static Task PublishPopulation(
        NpgsqlTransaction transaction,
        string table,
        string[] keys,
        string? entityTable,
        Guid artist,
        IEnumerable<JObject> rows,
        DateTime generatedAt,
        CancellationToken cancellationToken
    )
    {
        var columns = Columns + ",plays_1y,hours_1y,hot_score_1y";
        var fields = columns.Split(',');
        var selected = string.Join(',', fields.Select(column => "x." + column));
        var assignments = string.Join(',', fields.Select(column => $"{column}=EXCLUDED.{column}"));
        var qualifiedKeys = string.Join(',', keys.Select(key => $"x.{key}"));
        var matchingKeys = string.Join(" AND ", keys.Select(key => $"x.{key}=p.{key}"));
        var differingFields = string.Join(
            " OR ",
            fields.Select(column => $"p.{column} IS DISTINCT FROM EXCLUDED.{column}")
        );
        var ownerJoin = entityTable is null
            ? ""
            : $"JOIN {entityTable} entity ON entity.uuid=x.uuid JOIN artists a ON a.id=entity.artist_id AND a.uuid=@artist";

        return transaction.Connection!.ExecuteAsync(
            new CommandDefinition(
                $"""
                WITH input AS MATERIALIZED (
                    SELECT x.* FROM jsonb_to_recordset(@rows::jsonb)
                      AS x(uuid uuid,song_id text,{MetricInputColumns}) {ownerJoin}
                    WHERE x.plays_1y > 0 OR x.plays_30d > 0 OR x.plays_7d > 0 OR x.plays_48h > 0
                ), published AS (
                    INSERT INTO {table} AS p({string.Join(
                    ',',
                    keys
                )},artist_uuid,{columns},generated_at)
                    SELECT {qualifiedKeys},@artist,{selected},@generatedAt
                    FROM input x
                    ORDER BY {qualifiedKeys}
                    ON CONFLICT({string.Join(',', keys)}) DO UPDATE SET
                        artist_uuid=EXCLUDED.artist_uuid,{assignments},generated_at=EXCLUDED.generated_at
                    WHERE p.artist_uuid IS DISTINCT FROM EXCLUDED.artist_uuid OR {differingFields}
                )
                DELETE FROM {table} p
                WHERE p.artist_uuid=@artist
                  AND NOT EXISTS(SELECT 1 FROM input x WHERE {matchingKeys})
                """,
                new
                {
                    rows = JsonConvert.SerializeObject(rows),
                    artist,
                    generatedAt,
                },
                transaction,
                commandTimeout: 600,
                cancellationToken: cancellationToken
            )
        );
    }

    internal static async Task<
        Dictionary<(Guid Show, string Song), PopularityMetrics>
    > ReadPerformanceMetrics(
        IDbConnection connection,
        Guid[] shows,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        if (shows.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<StoredPopularity>(
            new CommandDefinition(
                $"SELECT {StoredPopularity.ReadColumns},p.song_id FROM song_performance_popularity p WHERE p.uuid=ANY(@shows) AND {StoredPopularity.MetadataFilter}",
                new { shows },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return PerformanceMap(rows);
    }

    internal static async Task<Dictionary<Guid, PopularityMetrics>> ReadSongPerformanceMetrics(
        IDbConnection connection,
        string song,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        var rows = await connection.QueryAsync<StoredPopularity>(
            new CommandDefinition(
                $"SELECT {StoredPopularity.ReadColumns} FROM song_performance_popularity p WHERE p.song_id=@song AND {StoredPopularity.MetadataFilter}",
                new { song },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return rows.ToDictionary(row => row.uuid, row => row.ToMetrics()!);
    }

    internal static async Task<
        Dictionary<(Guid Show, string Song), PopularityMetrics>
    > ReadSelectedPerformances(
        IDbConnection connection,
        Guid[] shows,
        string[] songs,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        var rows = await connection.QueryAsync<StoredPopularity>(
            new CommandDefinition(
                $"""
                SELECT {StoredPopularity.ReadColumns},p.song_id
                FROM unnest(@shows::uuid[],@songs::text[]) wanted(show,song)
                JOIN song_performance_popularity p ON p.uuid=wanted.show AND p.song_id=wanted.song
                WHERE {StoredPopularity.MetadataFilter}
                """,
                new { shows, songs },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return PerformanceMap(rows);
    }

    private static Dictionary<(Guid Show, string Song), PopularityMetrics> PerformanceMap(
        IEnumerable<StoredPopularity> rows
    ) => rows.ToDictionary(row => (row.uuid, row.song_id!), row => row.ToMetrics()!);

    internal static async Task<Dictionary<string, PopularityMetrics>> ReadSongMetrics(
        IDbConnection connection,
        string[] songs,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        if (songs.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<StoredPopularity>(
            new CommandDefinition(
                $"SELECT {StoredPopularity.MetricColumns},p.song_id FROM song_popularity p WHERE p.song_id=ANY(@songs) AND {StoredPopularity.MetadataFilter}",
                new { songs },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return rows.ToDictionary(row => row.song_id!, row => row.ToMetrics()!);
    }

    internal static async Task<Dictionary<Guid, PopularityMetrics>> ReadRecordingMetrics(
        IDbConnection connection,
        Guid[] sources,
        IDbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        if (sources.Length == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<StoredPopularity>(
            new CommandDefinition(
                $"SELECT {StoredPopularity.ReadColumns} FROM recording_popularity p WHERE p.uuid=ANY(@sources) AND {StoredPopularity.MetadataFilter}",
                new { sources },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return rows.ToDictionary(row => row.uuid, row => row.ToMetrics()!);
    }
}
